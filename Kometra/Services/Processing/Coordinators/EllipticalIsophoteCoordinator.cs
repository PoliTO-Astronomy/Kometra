using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Kometra.Models.Fits;
using Kometra.Models.Fits.Structure;
using Kometra.Models.Processing.Analysis;
using Kometra.Services.Fits;
using Kometra.Services.Fits.Conversion;
using Kometra.Services.Processing.Engines;
using OpenCvSharp;

namespace Kometra.Services.Processing.Coordinators;

public class EllipticalIsophoteCoordinator : IEllipticalIsophoteCoordinator
{
    private readonly IEllipticalIsophoteEngine _engine;
    private readonly IFitsDataManager _dataManager;
    private readonly IFitsOpenCvConverter _converter;

    public EllipticalIsophoteCoordinator(
        IEllipticalIsophoteEngine engine,
        IFitsDataManager dataManager,
        IFitsOpenCvConverter converter)
    {
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
        _dataManager = dataManager ?? throw new ArgumentNullException(nameof(dataManager));
        _converter = converter ?? throw new ArgumentNullException(nameof(converter));
    }

    public async Task<(List<EllipticalIsophoteDataPoint> Isophotes, string PreviewFilePath)> AnalyzeAndGenerateModelAsync(
        FitsFileReference sourceFile,
        double centerX,
        double centerY,
        double maxRadius,
        double stepSize,
        double ellipticity,
        double positionAngleDeg,
        CancellationToken cancellationToken = default)
    {
        return await Task.Run(async () =>
        {
            cancellationToken.ThrowIfCancellationRequested();

            var dataPackage = await _dataManager.LoadDataPackageAsync(sourceFile.FilePath);
            var hdu = dataPackage?.FirstImageHdu ?? dataPackage?.PrimaryHdu;
            if (hdu == null)
            {
                throw new InvalidOperationException("Nessun HDU immagine trovato nel file FITS specificato.");
            }

            using Mat srcMat = _dataManager.GetMatFromHdu(hdu);
            int rows = srcMat.Rows;
            int cols = srcMat.Cols;

            double paRad = positionAngleDeg * (Math.PI / 180.0);

            // 1. Calcolo del profilo di isofote tramite l'Engine
            var isophotes = _engine.AnalyzeProfile(srcMat, centerX, centerY, maxRadius, stepSize, ellipticity, paRad);

            cancellationToken.ThrowIfCancellationRequested();

            // 2. Generazione del Modello 2D
            using Mat modelMat = _engine.Generate2DEllipticalModel(rows, cols, centerX, centerY, isophotes, ellipticity, paRad);

            Array rawPixels = _converter.MatToRaw(modelMat, FitsBitDepth.Float);
            var header = hdu.Header ?? new FitsHeader();

            string outputDir = Path.Combine(Path.GetTempPath(), "Kometra", "EllipticalModels");
            Directory.CreateDirectory(outputDir);
            string tempPath = Path.Combine(outputDir, $"EllipticalModel_{Guid.NewGuid():N}.fits");

            // 3. Salvataggio del FITS temporaneo
            await _dataManager.SaveDataAsync(tempPath, rawPixels, header);

            return (isophotes, tempPath);
        }, cancellationToken);
    }
}