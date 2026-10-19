using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Kometra.Models.Fits;
using Kometra.Models.Fits.Structure;
using Kometra.Models.Processing.Analysis;
using Kometra.Services.Fits;
using Kometra.Services.Processing.Engines;
using OpenCvSharp;

namespace Kometra.Services.Processing.Coordinators;

public class RadialProfileCoordinator : IRadialProfileCoordinator
{
    private readonly IFitsDataManager _dataManager;
    private readonly IRadialProfileEngine _engine;

    public RadialProfileCoordinator(IFitsDataManager dataManager, IRadialProfileEngine engine)
    {
        _dataManager = dataManager ?? throw new ArgumentNullException(nameof(dataManager));
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
    }

    public async Task<IReadOnlyList<RadialProfileDataPoint>> AnalyzeProfileAsync(
        FitsFileReference file, 
        RadialProfileParameters parameters, 
        CancellationToken cancellationToken = default)
    {
        if (file == null)
            throw new ArgumentNullException(nameof(file));

        return await Task.Run(async () =>
        {
            cancellationToken.ThrowIfCancellationRequested();

            var dataPackage = await _dataManager.LoadDataPackageAsync(file.FilePath);
            var hdu = dataPackage?.FirstImageHdu ?? dataPackage?.PrimaryHdu;
            if (hdu == null)
                return Array.Empty<RadialProfileDataPoint>();

            using Mat imageMat = _dataManager.GetMatFromHdu(hdu);
            
            cancellationToken.ThrowIfCancellationRequested();

            return _engine.CalculateProfile(imageMat, parameters);
        }, cancellationToken);
    }
}