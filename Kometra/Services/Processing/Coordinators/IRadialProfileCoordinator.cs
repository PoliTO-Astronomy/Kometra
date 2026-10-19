using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Kometra.Models.Fits;
using Kometra.Models.Fits.Structure;
using Kometra.Models.Processing.Analysis;

namespace Kometra.Services.Processing.Coordinators;

public interface IRadialProfileCoordinator
{
    // Calcola il profilo radiale partendo dal file FITS e dai parametri di configurazione.
    Task<IReadOnlyList<RadialProfileDataPoint>> AnalyzeProfileAsync(
        FitsFileReference file, 
        RadialProfileParameters parameters, 
        CancellationToken cancellationToken = default);
}