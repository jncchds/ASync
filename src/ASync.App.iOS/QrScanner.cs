using AVFoundation;
using CoreFoundation;
using Foundation;
using UIKit;

namespace ASync.App.iOS;

/// <summary>
/// Scans the pairing QR code with the camera: AVFoundation's metadata output reading QR codes from the capture session
/// (the system's own decoder; no third-party SDK), in a full-screen view with the camera's picture and a Cancel button.
/// The camera permission is asked the first time (NSCameraUsageDescription). The simulator has no camera: the button is
/// not shown there.
/// </summary>
public static class QrScanner
{
    public static bool HasCamera => AVCaptureDevice.GetDefaultDevice(AVMediaTypes.Video) is not null;

    public static async Task<string?> ScanAsync()
    {
        var status = AVCaptureDevice.GetAuthorizationStatus(AVAuthorizationMediaType.Video);
        if (status == AVAuthorizationStatus.NotDetermined)
            status = await AVCaptureDevice.RequestAccessForMediaTypeAsync(AVAuthorizationMediaType.Video)
                ? AVAuthorizationStatus.Authorized : AVAuthorizationStatus.Denied;
        if (status != AVAuthorizationStatus.Authorized)
            throw new InvalidOperationException("ASync may not use the camera: allow it in Settings → ASync → Camera, or type the code.");
        var done = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        UIApplication.SharedApplication.InvokeOnMainThread(() =>
        {
            if (IosPlatform.Top() is not { } top)
            {
                done.TrySetResult(null);
                return;
            }
            var controller = new ScannerController(done) { ModalPresentationStyle = UIModalPresentationStyle.FullScreen };
            top.PresentViewController(controller, true, null);
        });
        return await done.Task;
    }

    private sealed class ScannerController(TaskCompletionSource<string?> done) : UIViewController, IAVCaptureMetadataOutputObjectsDelegate
    {
        private AVCaptureSession? _session;
        private AVCaptureVideoPreviewLayer? _preview;

        public override void ViewDidLoad()
        {
            base.ViewDidLoad();
            View!.BackgroundColor = UIColor.Black;
            var session = new AVCaptureSession();
            var device = AVCaptureDevice.GetDefaultDevice(AVMediaTypes.Video);
            var input = device is null ? null : AVCaptureDeviceInput.FromDevice(device, out _);
            var output = new AVCaptureMetadataOutput();
            if (input is null || !session.CanAddInput(input) || !session.CanAddOutput(output))
            {
                Finish(null);
                return;
            }
            session.AddInput(input);
            session.AddOutput(output);
            output.SetDelegate(this, DispatchQueue.MainQueue);
            output.MetadataObjectTypes = AVMetadataObjectType.QRCode;
            _preview = new AVCaptureVideoPreviewLayer(session) { VideoGravity = AVLayerVideoGravity.ResizeAspectFill, Frame = View.Bounds };
            View.Layer.AddSublayer(_preview);

            var hint = new UILabel
            {
                Text = "Point the camera at the QR code under Devices → Add device in the web UI.",
                TextColor = UIColor.White,
                Lines = 0,
                TextAlignment = UITextAlignment.Center,
                TranslatesAutoresizingMaskIntoConstraints = false,
            };
            var cancel = UIButton.FromType(UIButtonType.System);
            cancel.SetTitle("Cancel", UIControlState.Normal);
            cancel.SetTitleColor(UIColor.White, UIControlState.Normal);
            cancel.TranslatesAutoresizingMaskIntoConstraints = false;
            cancel.TouchUpInside += (_, _) => Finish(null);
            View.AddSubviews(hint, cancel);
            var guide = View.SafeAreaLayoutGuide;
            NSLayoutConstraint.ActivateConstraints(
            [
                hint.LeadingAnchor.ConstraintEqualTo(guide.LeadingAnchor, 24),
                hint.TrailingAnchor.ConstraintEqualTo(guide.TrailingAnchor, -24),
                hint.TopAnchor.ConstraintEqualTo(guide.TopAnchor, 24),
                cancel.CenterXAnchor.ConstraintEqualTo(guide.CenterXAnchor),
                cancel.BottomAnchor.ConstraintEqualTo(guide.BottomAnchor, -24),
            ]);
            _session = session;
            // startRunning blocks until the camera runs: off the main thread.
            DispatchQueue.GetGlobalQueue(DispatchQueuePriority.Default).DispatchAsync(session.StartRunning);
        }

        public override void ViewDidLayoutSubviews()
        {
            base.ViewDidLayoutSubviews();
            if (_preview is not null) _preview.Frame = View!.Bounds;
        }

        [Export("captureOutput:didOutputMetadataObjects:fromConnection:")]
        public void DidOutputMetadataObjects(AVCaptureMetadataOutput captureOutput, AVMetadataObject[] metadataObjects, AVCaptureConnection connection)
        {
            foreach (var found in metadataObjects)
                if (found is AVMetadataMachineReadableCodeObject { StringValue: { Length: > 0 } text })
                {
                    Finish(text);
                    return;
                }
        }

        private void Finish(string? text)
        {
            if (!done.TrySetResult(text)) return;
            var session = _session;
            _session = null;
            if (session is not null) DispatchQueue.GetGlobalQueue(DispatchQueuePriority.Default).DispatchAsync(session.StopRunning);
            DismissViewController(true, null);
        }
    }
}
