using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Kometra.Models.Fits;
using Kometra.Models.Processing.Analysis;

namespace Kometra.Services.Processing.Coordinators;

public interface IPhotometricProfileCoordinator
{
    Task<IReadOnlyList<PhotometricDataPoint>> AnalyzeProfileAsync(FitsFileReference file, PhotometricCutParameters parameters, CancellationToken cancellationToken = default);
}