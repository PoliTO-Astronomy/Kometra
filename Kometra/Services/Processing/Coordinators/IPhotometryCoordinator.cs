using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Kometra.Models.Fits;
using Kometra.Models.Processing.Analysis;
using Kometra.Models.Processing.Batch;
using OpenCvSharp;

namespace Kometra.Services.Processing.Coordinators;

public interface IPhotometryCoordinator
{
    Task<Mat> GeneratePreviewAsync(FitsFileReference file, PhotometricClippingParameters parameters, CancellationToken token = default);

    Task<List<FitsFileReference>> ExecuteBatchAsync(List<FitsFileReference> files, PhotometricClippingParameters parameters, IProgress<BatchProgressReport>? progress = null, CancellationToken token = default);
}