using System.Threading;
using System.Threading.Tasks;
using Kometra.Models.Fits;
using Kometra.Models.Processing.Analysis;

namespace Kometra.Services.Processing.Coordinators;

public interface IEllipticalIsophoteCoordinator
{
    Task<IsophoteAnalysisResult> AnalyzeProfileAsync(FitsFileReference file, EllipticalIsophoteParameters parameters, CancellationToken cancellationToken = default);
}