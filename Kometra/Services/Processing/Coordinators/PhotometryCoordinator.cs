using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Kometra.Models.Fits;
using Kometra.Models.Fits.Structure;
using Kometra.Models.Processing.Analysis;
using Kometra.Models.Processing.Batch;
using Kometra.Services.Fits;
using Kometra.Services.Fits.Conversion;
using Kometra.Services.Fits.Metadata;
using Kometra.Services.Processing.Engines;
using OpenCvSharp;

namespace Kometra.Services.Processing.Coordinators;

public class PhotometryCoordinator : IPhotometryCoordinator
{
    private readonly IFitsDataManager _dataManager;
    private readonly IPhotometryEngine _photometryEngine;
    private readonly IFitsMetadataService _metadataService;
    private readonly IFitsOpenCvConverter _converter;

    public PhotometryCoordinator(
        IFitsDataManager dataManager,
        IPhotometryEngine photometryEngine,
        IFitsMetadataService metadataService,
        IFitsOpenCvConverter converter)
    {
        _dataManager = dataManager;
        _photometryEngine = photometryEngine;
        _metadataService = metadataService;
        _converter = converter;
    }

    public async Task<Mat> GeneratePreviewAsync(FitsFileReference file, PhotometricClippingParameters parameters, CancellationToken token = default)
    {
        var dataPackage = await _dataManager.GetDataAsync(file.FilePath);
        var imageHdu = dataPackage.FirstImageHdu ?? dataPackage.PrimaryHdu;
        if (imageHdu == null) throw new InvalidOperationException("Nessuna immagine trovata.");

        var header = file.ModifiedHeader ?? imageHdu.Header;
        double bScale = _metadataService.GetDoubleValue(header, "BSCALE", 1.0);
        double bZero = _metadataService.GetDoubleValue(header, "BZERO", 0.0);

        using Mat sourceMat = _converter.RawToMat(imageHdu.PixelData, bScale, bZero, FitsBitDepth.Float);
        token.ThrowIfCancellationRequested();

        return _photometryEngine.ApplyPhotometricClipping(sourceMat, parameters);
    }

    public async Task<List<FitsFileReference>> ExecuteBatchAsync(
        List<FitsFileReference> files, 
        PhotometricClippingParameters parameters, 
        IProgress<BatchProgressReport>? progress = null, 
        CancellationToken token = default)
    {
        var processedFiles = new List<FitsFileReference>();
        int totalFiles = files.Count;
        int completedFiles = 0;

        foreach (var file in files)
        {
            token.ThrowIfCancellationRequested();

            var dataPackage = await _dataManager.GetDataAsync(file.FilePath);
            var imageHdu = dataPackage.FirstImageHdu ?? dataPackage.PrimaryHdu;
            if (imageHdu == null) continue;

            var header = file.ModifiedHeader ?? imageHdu.Header;
            double bScale = _metadataService.GetDoubleValue(header, "BSCALE", 1.0);
            double bZero = _metadataService.GetDoubleValue(header, "BZERO", 0.0);

            using Mat sourceMat = _converter.RawToMat(imageHdu.PixelData, bScale, bZero, FitsBitDepth.Float);
            using Mat processedMat = _photometryEngine.ApplyPhotometricClipping(sourceMat, parameters);

            Array newPixelData = _converter.MatToRaw(processedMat, FitsBitDepth.Float);

            FitsHeader newHeader = header.Clone();
            _metadataService.AddValue(newHeader, "BSCALE", "1.0", "Physical = FITS * BSCALE + BZERO");
            _metadataService.AddValue(newHeader, "BZERO", "0.0", "Physical = FITS * BSCALE + BZERO");
            _metadataService.AddValue(newHeader, "HISTORY", $"Photometric Clipping applied. Output={parameters.OutputMode}", null);

            // QUI LA CORREZIONE: Usiamo il metodo nativo di Kometra per i file temporanei
            FitsFileReference tempFileRef = await _dataManager.SaveAsTemporaryAsync(newPixelData, newHeader, "PhotometricClipping");
            
            processedFiles.Add(tempFileRef);

            completedFiles++;
            
            double percentage = (double)completedFiles / totalFiles * 100.0;
            progress?.Report(new BatchProgressReport(completedFiles, totalFiles, tempFileRef.FileName, percentage));
        }

        processedFiles.Sort((a, b) => string.Compare(a.FileName, b.FileName, StringComparison.OrdinalIgnoreCase));
        return processedFiles;
    }
}