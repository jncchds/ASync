using Android;
using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.Graphics;
using Android.OS;
using Android.Runtime;
using Android.Views;
using Android.Widget;
using ZXing;
using Camera = Android.Hardware.Camera;
using Result = Android.App.Result;
using ZXing.Common;
using ZXing.QrCode;
#pragma warning disable CA1422, CS0618 // android.hardware.Camera: deprecated, but on every Android version and without extra libraries

namespace ASync.App.Android;

/// <summary>
/// Scans the QR code of the web UI's Devices → Add device: the camera's preview frames go to ZXing.Net's QR reader (pure
/// .NET, no Google Play services). Returns the code's text in <see cref="ResultText"/>; Cancel or Back returns nothing.
/// Uses the old <c>android.hardware.Camera</c> API on purpose: it exists on every supported version and needs no
/// AndroidX camera libraries; a preview and a callback per frame are all a QR scanner needs.
/// </summary>
[Activity(Name = "ua.pp.chds.async.QrScanActivity", Label = "Scan the QR code", Theme = "@style/ASyncScannerTheme", Exported = false, ScreenOrientation = ScreenOrientation.Portrait)]
public sealed class QrScanActivity : Activity, ISurfaceHolderCallback, Camera.IPreviewCallback
{
    public const string ResultText = "text";
    private const int CameraRequest = 7;

    private SurfaceView? _surface;
    private TextView? _hint;
    private Camera? _camera;
    private int _decoding;
    private bool _done;
    private readonly QRCodeReader _reader = new();
    private static readonly Dictionary<DecodeHintType, object> Hints = new() { [DecodeHintType.TRY_HARDER] = true };

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        var root = new FrameLayout(this);
        _surface = new SurfaceView(this);
        root.AddView(_surface, new FrameLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.MatchParent));

        var panel = new LinearLayout(this) { Orientation = Orientation.Vertical };
        panel.SetBackgroundColor(Color.Argb(200, 30, 18, 38));
        panel.SetPadding(Dp(20), Dp(16), Dp(20), Dp(20));
        var title = new TextView(this) { Text = "Scan the QR code" };
        title.SetTextColor(Color.White);
        title.TextSize = 20;
        title.SetTypeface(null, TypefaceStyle.Bold);
        _hint = new TextView(this) { Text = "In the web UI: Devices → Add device. Point the camera at the code." };
        _hint.SetTextColor(Color.Argb(230, 255, 255, 255));
        _hint.TextSize = 15;
        _hint.SetPadding(0, Dp(6), 0, Dp(12));
        var cancel = new Button(this) { Text = "Type the code instead" };
        cancel.Click += (_, _) => Finish();
        panel.AddView(title);
        panel.AddView(_hint);
        panel.AddView(cancel);
        root.AddView(panel, new FrameLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent, GravityFlags.Bottom));
        // Android 15 draws apps edge to edge: keep the panel above the navigation bar.
        panel.SetOnApplyWindowInsetsListener(new BottomInset(Dp(20)));

        // A frame to aim with.
        var frame = new View(this);
        var box = new global::Android.Graphics.Drawables.GradientDrawable();
        box.SetStroke(Dp(3), Color.Argb(255, 217, 70, 239));
        box.SetCornerRadius(Dp(16));
        frame.Background = box;
        root.AddView(frame, new FrameLayout.LayoutParams(Dp(240), Dp(240), GravityFlags.Center));

        SetContentView(root);
        SetResult(Result.Canceled);
        if (CheckSelfPermission(Manifest.Permission.Camera) == Permission.Granted) _surface.Holder!.AddCallback(this);
        else RequestPermissions([Manifest.Permission.Camera], CameraRequest);
    }

    /// <summary>Pads a view's bottom by the navigation bar's height (plus its own padding).</summary>
    private sealed class BottomInset(int basePadding) : Java.Lang.Object, View.IOnApplyWindowInsetsListener
    {
        public WindowInsets OnApplyWindowInsets(View v, WindowInsets insets)
        {
            var bottom = OperatingSystem.IsAndroidVersionAtLeast(30)
                ? insets.GetInsets(WindowInsets.Type.SystemBars()).Bottom
#pragma warning disable CA1422 // the API 26–29 way
                : insets.SystemWindowInsetBottom;
#pragma warning restore CA1422
            v.SetPadding(v.PaddingLeft, v.PaddingTop, v.PaddingRight, basePadding + bottom);
            return insets;
        }
    }

    private int Dp(int dp) => (int)(dp * (Resources?.DisplayMetrics?.Density ?? 2));

    public override void OnRequestPermissionsResult(int requestCode, string[] permissions, [GeneratedEnum] Permission[] grantResults)
    {
        base.OnRequestPermissionsResult(requestCode, permissions, grantResults);
        if (requestCode != CameraRequest) return;
        if (grantResults.Length > 0 && grantResults[0] == Permission.Granted)
        {
            _surface!.Holder!.AddCallback(this);
            if (_surface.Holder.Surface is { IsValid: true }) StartCamera(_surface.Holder);
        }
        else
        {
            Toast.MakeText(this, "Without the camera, type the code from the web UI instead.", ToastLength.Long)?.Show();
            Finish();
        }
    }

    public void SurfaceCreated(ISurfaceHolder holder) => StartCamera(holder);

    public void SurfaceChanged(ISurfaceHolder holder, [GeneratedEnum] Format format, int width, int height) { }

    public void SurfaceDestroyed(ISurfaceHolder holder) => StopCamera();

    private void StartCamera(ISurfaceHolder holder)
    {
        if (_camera is not null) return;
        try
        {
            _camera = Camera.Open(); // the first back-facing camera
            if (_camera is null && Camera.NumberOfCameras > 0) _camera = Camera.Open(0);
            if (_camera is null) throw new InvalidOperationException("This device has no camera.");
            var parameters = _camera.GetParameters()!;
            if (parameters.SupportedFocusModes?.Contains(Camera.Parameters.FocusModeContinuousPicture) == true)
                parameters.FocusMode = Camera.Parameters.FocusModeContinuousPicture;
            _camera.SetParameters(parameters);
            _camera.SetDisplayOrientation(90);
            _camera.SetPreviewDisplay(holder);
            _camera.SetPreviewCallback(this);
            _camera.StartPreview();
        }
        catch (Exception e) when (e is Java.Lang.Exception or InvalidOperationException)
        {
            if (_hint is not null) _hint.Text = $"The camera could not be opened ({e.Message}). Type the code instead.";
            StopCamera();
        }
    }

    private void StopCamera()
    {
        if (_camera is not { } camera) return;
        _camera = null;
        try
        {
            camera.SetPreviewCallback(null);
            camera.StopPreview();
        }
        catch (Java.Lang.Exception) { }
        camera.Release();
    }

    protected override void OnPause()
    {
        StopCamera();
        base.OnPause();
    }

    protected override void OnResume()
    {
        base.OnResume();
        if (_surface?.Holder?.Surface is { IsValid: true } && CheckSelfPermission(Manifest.Permission.Camera) == Permission.Granted)
            StartCamera(_surface.Holder);
    }

    public void OnPreviewFrame(byte[]? data, Camera? camera)
    {
        if (data is null || camera is null || _done || Interlocked.Exchange(ref _decoding, 1) == 1) return;
        var size = camera.GetParameters()?.PreviewSize;
        if (size is null) { _decoding = 0; return; }
        var (width, height) = (size.Width, size.Height);
        Task.Run(() =>
        {
            try
            {
                if (Decode(data, width, height) is { } text) RunOnUiThread(() => Finish(text));
            }
            finally
            {
                Volatile.Write(ref _decoding, 0);
            }
        });
    }

    /// <summary>The QR code in an NV21 frame (the Y plane is the luminance ZXing needs), or null.</summary>
    internal string? Decode(byte[] nv21, int width, int height)
    {
        try
        {
            var source = new PlanarYUVLuminanceSource(nv21, width, height, 0, 0, width, height, false);
            return _reader.decode(new BinaryBitmap(new HybridBinarizer(source)), Hints)?.Text;
        }
        catch (Exception e) when (e is ReaderException or ArgumentException)
        {
            return null;
        }
    }

    private void Finish(string text)
    {
        if (_done) return;
        _done = true;
        SetResult(Result.Ok, new Intent().PutExtra(ResultText, text));
        Finish();
    }
}
