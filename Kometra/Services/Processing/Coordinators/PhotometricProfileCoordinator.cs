using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Kometra.Models.Fits;
using Kometra.Models.Processing.Analysis;
using Kometra.Services.Fits;
using Kometra.Services.Processing.Engines;
using OpenCvSharp;

namespace Kometra.Services.Processing.Coordinators;

public class PhotometricProfileCoordinator : IPhotometricProfileCoordinator
{
    private readonly IFitsDataManager _dataManager;
    private readonly IPhotometricProfileEngine _engine;

    public PhotometricProfileCoordinator(IFitsDataManager dataManager, IPhotometricProfileEngine engine)
    {
        _dataManager = dataManager ?? throw new ArgumentNullException(nameof(dataManager));
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
    }

    public async Task<IReadOnlyList<PhotometricDataPoint>> AnalyzeProfileAsync(FitsFileReference file, PhotometricCutParameters parameters, CancellationToken cancellationToken = default)
    {
        return await Task.Run(async () =>
        {
            cancellationToken.ThrowIfCancellationRequested();

            var dataPackage = await _dataManager.LoadDataPackageAsync(file.FilePath);
            var hdu = dataPackage?.FirstImageHdu ?? dataPackage?.PrimaryHdu;
            if (hdu == null) return Array.Empty<PhotometricDataPoint>();

            using Mat srcMat = _dataManager.GetMatFromHdu(hdu);
            using Mat floatMat = new Mat();
            if (srcMat.Type() != MatType.CV_32FC1) srcMat.ConvertTo(floatMat, MatType.CV_32FC1);
            else srcMat.CopyTo(floatMat);

            cancellationToken.ThrowIfCancellationRequested();
            return _engine.CalculateProfile(floatMat, parameters);
            
        }, cancellationToken);
    }
}