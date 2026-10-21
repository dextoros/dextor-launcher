namespace DexTor.Launcher;

internal sealed class PreparationBundleService(
    LiveImagePreparationService imageService,
    QemuRuntimePreparationService runtimeService,
    string imageCacheRoot,
    string runtimeCacheRoot)
{
    internal async Task<LiveImageCacheInspectionResult> InspectAsync(
        CancellationToken cancellationToken,
        IProgress<LiveImagePreparationProgress> progress)
    {
        var image = await imageService.InspectCacheFromIndexAsync(imageCacheRoot, progress, cancellationToken);
        if (!image.Success)
        {
            return image;
        }

        var runtime = runtimeService.Inspect(runtimeCacheRoot);
        return image with
        {
            IsReady = image.IsReady && runtime.IsReady,
            CacheRoot = Path.GetDirectoryName(imageCacheRoot) ?? imageCacheRoot
        };
    }

    internal async Task<LiveImagePreparationResult> PrepareAsync(
        CancellationToken cancellationToken,
        IProgress<LiveImagePreparationProgress> progress)
    {
        var image = await imageService.PrepareFromIndexAsync(imageCacheRoot, progress, cancellationToken);
        if (!image.Success)
        {
            return image;
        }

        QemuRuntimeInspectionResult runtime;
        try
        {
            runtime = await runtimeService.PrepareAsync(runtimeCacheRoot, progress, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            runtime = new QemuRuntimeInspectionResult(
                false,
                Path.Combine(runtimeCacheRoot, $"qemu-{QemuRuntimePreparationService.Version}"),
                exception.Message);
        }
        return runtime.IsReady
            ? image
            : image with
            {
                Success = false,
                ErrorMessage = runtime.ErrorMessage ?? "Could not prepare the Windows virtualization runtime.",
                ErrorMessageKey = "launcher.create.live_image.prepare.failed"
            };
    }
}
