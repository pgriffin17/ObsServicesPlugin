using NINA.Astrometry;
using NINA.Core.Enum;
using NINA.Core.Model;
using NINA.Core.Model.Equipment;
using NINA.Core.Utility;
using NINA.Equipment.Interfaces.Mediator;
using NINA.Equipment.Model;
using NINA.Profile.Interfaces;
using NINA.WPF.Base.Interfaces.Mediator;
using ObsServices.Core.Devices;

namespace ObsServicesPlugin.Nina;

/// <summary>
/// The N.I.N.A. camera as the core sees it. Frames go through the same path as the sequencer's
/// "Take Exposure": capture via the imaging mediator, then the image save pipeline with the target
/// written into the metadata, so file naming, plate solving and image history all see them.
/// </summary>
public sealed class NinaCamera : ICameraDevice {
    private readonly ICameraMediator camera;
    private readonly IImagingMediator imaging;
    private readonly IImageSaveMediator imageSave;
    private readonly IProfileService profiles;
    private readonly object gate = new();
    private DateTime exposureEnd;
    private double exposureSeconds;
    private bool exposing;

    public NinaCamera(ICameraMediator camera, IImagingMediator imaging, IImageSaveMediator imageSave, IProfileService profiles) {
        this.camera = camera;
        this.imaging = imaging;
        this.imageSave = imageSave;
        this.profiles = profiles;
    }

    public CameraReading Read() {
        var info = camera.GetInfo();

        if (info == null || !info.Connected) {
            return CameraReading.Disconnected;
        }

        lock (gate) {
            var activity = info.CameraState switch {
                CameraStates.Exposing or CameraStates.Waiting => CameraActivity.Exposing,
                CameraStates.Reading or CameraStates.Download => CameraActivity.ReadingOut,
                _ => exposing ? CameraActivity.Exposing : CameraActivity.Idle,
            };
            var remaining = exposing ? Math.Max(0, (exposureEnd - DateTime.UtcNow).TotalSeconds) : 0;
            double? temperature = double.IsNaN(info.Temperature) ? null : info.Temperature;
            return new CameraReading(true, activity, temperature, exposing ? exposureSeconds : 0, remaining);
        }
    }

    public async Task ExposeAsync(ExposureRequest request, CancellationToken cancellationToken) {
        // The filter wheel is driven by the agent before the exposure, so no FilterType here.
        var sequence = new CaptureSequence(request.Seconds, CaptureSequence.ImageTypes.LIGHT, null, new BinningMode(1, 1), 1) {
            ProgressExposureCount = request.FrameIndex + 1,
            TotalExposureCount = request.FrameCount,
        };
        var progress = new Progress<ApplicationStatus>();

        lock (gate) {
            exposing = true;
            exposureSeconds = request.Seconds;
            exposureEnd = DateTime.UtcNow + TimeSpan.FromSeconds(request.Seconds);
        }

        try {
            var exposureData = await imaging.CaptureImage(sequence, cancellationToken, progress, request.TargetName);
            var imageData = await exposureData.ToImageData(progress, cancellationToken);
            imageData.MetaData.Target.Name = request.TargetName;
            imageData.MetaData.Target.Coordinates = new Coordinates(Angle.ByDegree(request.RaJ2000Deg), Angle.ByDegree(request.DecJ2000Deg), Epoch.J2000);
            imageData.MetaData.Sequence.Title = request.TaskName;
            var prepared = imaging.PrepareImage(imageData, new PrepareImageParameters(true, true), cancellationToken);
            await imageSave.Enqueue(imageData, prepared, progress, cancellationToken);
        } catch (OperationCanceledException) {
            camera.AbortExposure();
            throw;
        } finally {
            lock (gate) {
                exposing = false;
            }
        }
    }
}
