using System;
using System.Threading;
using System.Threading.Tasks;
using Kometra.Models.Fits;
using Kometra.Models.Processing.Analysis;
using Kometra.Services.Fits;
using Kometra.Services.Processing.Engines;
using OpenCvSharp;

namespace Kometra.Services.Processing.Coordinators;

public class EllipticalIsophoteCoordinator : IEllipticalIsophoteCoordinator
{
    private readonly IFitsDataManager _dataManager;
    private readonly IEllipticalIsophoteEngine _engine;

    public EllipticalIsophoteCoordinator(IFitsDataManager dataManager, IEllipticalIsophoteEngine engine)
    {
        _dataManager = dataManager ?? throw new ArgumentNullException(nameof(dataManager));
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
    }

    public async Task<IsophoteAnalysisResult> AnalyzeProfileAsync(FitsFileReference file, EllipticalIsophoteParameters parameters, CancellationToken cancellationToken = default)
    {
        return await Task.Run(async () =>
        {
            cancellationToken.ThrowIfCancellationRequested();

            var dataPackage = await _dataManager.LoadDataPackageAsync(file.FilePath);
            var hdu = dataPackage?.FirstImageHdu ?? dataPackage?.PrimaryHdu;
            if (hdu == null) return new IsophoteAnalysisResult();

            using Mat srcMat = _dataManager.GetMatFromHdu(hdu);
            using Mat floatMat = new Mat();
            
            if (srcMat.Type() != MatType.CV_32FC1) srcMat.ConvertTo(floatMat, MatType.CV_32FC1);
            else srcMat.CopyTo(floatMat);

            Cv2.PatchNaNs(floatMat, 0.0);

            cancellationToken.ThrowIfCancellationRequested();
            return _engine.CalculateIsophotes(floatMat, parameters);

        }, cancellationToken);
    }
}