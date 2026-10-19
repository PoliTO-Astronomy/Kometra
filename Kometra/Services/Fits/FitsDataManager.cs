using System;
using System.Collections.Concurrent;
using System.Collections.Generic; // Necessario per List<>
using System.IO;
using System.Linq; // Necessario per Sum() e Linq
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Kometra.Models.Fits;
using Kometra.Models.Fits.Structure;
using Kometra.Services.Fits.Conversion; // Necessario per IFitsOpenCvConverter
using Kometra.Services.Fits.IO;
using Microsoft.Extensions.Caching.Memory;
using OpenCvSharp; // Necessario per Mat

namespace Kometra.Services.Fits;

public class FitsDataManager : IFitsDataManager
{
    private readonly IFitsIoService _ioService;
    private readonly IMemoryCache _cache;
    private readonly IFitsOpenCvConverter _openCvConverter; //  Per la conversione in Mat
    
    private readonly ConcurrentBag<string> _tempFilesTracker = new();
    private static readonly TimeSpan CacheExpiration = TimeSpan.FromMinutes(10);

    // [MODIFICA COSTRUTTORE] Aggiunta iniezione di IFitsOpenCvConverter
    public FitsDataManager(IFitsIoService ioService, IMemoryCache cache, IFitsOpenCvConverter openCvConverter)
    {
        _ioService = ioService ?? throw new ArgumentNullException(nameof(ioService));
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));
        _openCvConverter = openCvConverter ?? throw new ArgumentNullException(nameof(openCvConverter));
    }

    // =======================================================================
    // 1. LETTURA (Logica di Caching Aggiornata per MEF)
    // =======================================================================

    public async Task<FitsDataPackage> GetDataAsync(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("Path vuoto");

        return await _cache.GetOrCreateAsync(path, async entry =>
        {
            entry.SlidingExpiration = CacheExpiration;

            //  Leggiamo TUTTI gli HDU invece di solo Header+Pixel fissi.
            // Nota: Casting esplicito a FitsIoService se l'interfaccia IFitsIoService 
            // non è stata ancora aggiornata con ReadAllHdusAsync. 
            // L'ideale è aver aver aggiornato anche l'interfaccia.
            var hdus = await ((FitsIoService)_ioService).ReadAllHdusAsync(path);

            if (hdus == null || hdus.Count == 0)
                throw new FileNotFoundException($"File non valido o vuoto: {path}");

            // [MODIFICA] Calcolo dimensione totale sommando tutti gli HDU
            long totalSize = hdus.Sum(h => CalculateArraySize(h.PixelData));
            entry.Size = totalSize;
            
            // Creiamo il pacchetto con la lista completa
            return new FitsDataPackage(path, hdus);
        }) ?? throw new InvalidOperationException("Errore cache");
    }

    public async Task<FitsHeader?> GetHeaderOnlyAsync(string path)
    {
        // Se il pacchetto completo è in cache, estraiamo l'header da lì senza rileggere il file
        if (_cache.TryGetValue(path, out FitsDataPackage? package) && package != null)
        {
            // Preferiamo l'header della prima immagine valida, altrimenti il primario
            return package.FirstImageHdu?.Header ?? package.PrimaryHdu?.Header;
        }

        // Se non è in cache, usiamo il metodo "Smart" del servizio IO che cerca la prima immagine
        return await _ioService.ReadHeaderAsync(path);
    }

    // =======================================================================
    // 2. SCRITTURA E GENERAZIONE TEMPORANEI
    // =======================================================================

    public async Task SaveDataAsync(string path, Array pixels, FitsHeader header)
    {
        // Scrittura fisica su disco (Single HDU per ora)
        await _ioService.WriteFileAsync(path, pixels, header);

        //  Per aggiornare la cache, dobbiamo creare un pacchetto
        // conforme alla nuova struttura (List<FitsHdu>).
        // Creiamo una lista con un singolo HDU.
        var singleHdu = new FitsHdu(header, pixels, false);
        var hdus = new List<FitsHdu> { singleHdu };

        var package = new FitsDataPackage(path, hdus);
        
        _cache.Set(path, package, new MemoryCacheEntryOptions 
        { 
            SlidingExpiration = CacheExpiration,
            Size = CalculateArraySize(pixels)
        });
    }

    public async Task<FitsFileReference> SaveAsTemporaryAsync(Array pixels, FitsHeader header, string context)
    {
        string fullPath = _ioService.BuildRawPath(context, $"{Guid.NewGuid()}.fits");

        await SaveDataAsync(fullPath, pixels, header);
        _tempFilesTracker.Add(fullPath);

        return new FitsFileReference(fullPath);
    }

    // =======================================================================
    // 3. GESTIONE SANDBOX (Copia Disco-Disco)
    // =======================================================================

    public async Task<string> CreateSandboxCopyAsync(string originalPath, string context)
    {
        string tempPath = _ioService.BuildRawPath(context, $"sandbox_{Guid.NewGuid()}.fits");
        await _ioService.CopyFileAsync(originalPath, tempPath);
        _tempFilesTracker.Add(tempPath);
        
        return tempPath;
    }

    // =======================================================================
    // 4. MANUTENZIONE E CLEANUP
    // =======================================================================

    public void Invalidate(string path) => _cache.Remove(path);

    public void DeleteTemporaryData(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;

        Invalidate(path);
        _ioService.TryDeleteFile(path);
    }

    public void ClearCache()
    {
        if (_cache is MemoryCache concreteCache)
        {
            concreteCache.Compact(1.0);
        }
    }

    public void Clear()
    {
        ClearCache();
        foreach (var tempPath in _tempFilesTracker)
        {
            _ioService.TryDeleteFile(tempPath);
        }
        while (_tempFilesTracker.TryTake(out _)) { }
    }

    // =======================================================================
    // 5. UTILITY DI CONVERSIONE E ACCESSO PACCHETTI [NUOVI METODI]
    // =======================================================================

    /// <summary>
    /// Alias convenzionale per GetDataAsync, così da supportare chiamate dirette al pacchetto completo.
    /// </summary>
    public async Task<FitsDataPackage?> LoadDataPackageAsync(string filePath)
    {
        return await GetDataAsync(filePath);
    }

    /// <summary>
    /// Estrae un oggetto Mat di OpenCvSharp partendo dal PixelData del singolo FitsHdu.
    /// </summary>
    public Mat GetMatFromHdu(FitsHdu hdu)
    {
        if (hdu == null || hdu.PixelData == null)
            throw new ArgumentNullException(nameof(hdu), "L'HDU specificato o i suoi dati pixel sono nulli.");

        // Chiama direttamente l'implementazione del converter per trasformare l'Array FITS in Mat
        return _openCvConverter.RawToMat(hdu.PixelData);
    }

    // =======================================================================
    // HELPERS DI CALCOLO DIMENSIONE
    // =======================================================================

    /// <summary>
    /// Calcola l'occupazione in byte dell'array di pixel per permettere 
    /// alla MemoryCache di rispettare il SizeLimit globale configurato.
    /// </summary>
    private long CalculateArraySize(Array array)
    {
        if (array == null) return 0;
        // Numero totale di elementi * dimensione in byte del tipo
        // Se l'array è vuoto (0x0), SizeOf potrebbe fallire o tornare 0, gestiamolo
        if (array.Length == 0) return 0;

        var elementType = array.GetType().GetElementType();
        if (elementType == null) return 0;

        return (long)array.Length * Marshal.SizeOf(elementType);
    }
}