using Android.App;
using Android.Content.PM;
using Android.OS;
using Android.Views;

namespace FlashCardsApp
{
    [Activity(Theme = "@style/Maui.SplashTheme", MainLauncher = true, LaunchMode = LaunchMode.SingleTop, ConfigurationChanges = ConfigChanges.ScreenSize | ConfigChanges.Orientation | ConfigChanges.UiMode | ConfigChanges.ScreenLayout | ConfigChanges.SmallestScreenSize | ConfigChanges.Density)]
    public class MainActivity : MauiAppCompatActivity
    {
        protected override void OnCreate(Bundle? savedInstanceState)
        {
            base.OnCreate(savedInstanceState);
            HideSystemBars();
        }

        public override bool DispatchKeyEvent(KeyEvent? e)
        {
            if (e is not null && e.Action == KeyEventActions.Down && e.RepeatCount == 0)
            {
                var page = Shell.Current?.CurrentPage as MainPage;
                if (page?.KeyboardShortcutsEnabled != true)
                    return base.DispatchKeyEvent(e);

                var direction = e.KeyCode switch
                {
                    Keycode.DpadLeft => -1,
                    Keycode.DpadRight => 1,
                    _ => 0
                };

                if (direction != 0 || e.KeyCode is Keycode.Space or Keycode.F11 or Keycode.Escape)
                {
                    Microsoft.Maui.ApplicationModel.MainThread.BeginInvokeOnMainThread(() =>
                    {
                        if (direction != 0)
                            page.HandleKeyboardNavigation(direction);
                        else if (e.KeyCode == Keycode.Space)
                            page.HandleKeyboardFlip();
                        else if (e.KeyCode == Keycode.F11)
                            page.HandleKeyboardMaximize();
                        else if (e.KeyCode == Keycode.Escape)
                            page.HandleKeyboardMinimize();
                    });
                    return true;
                }
            }

            return base.DispatchKeyEvent(e);
        }

        public override void OnWindowFocusChanged(bool hasFocus)
        {
            base.OnWindowFocusChanged(hasFocus);
            if (hasFocus)
                HideSystemBars();
        }

        private void HideSystemBars()
        {
            if (Build.VERSION.SdkInt >= BuildVersionCodes.R)
            {
                Window?.InsetsController?.Hide(WindowInsets.Type.StatusBars() | WindowInsets.Type.NavigationBars());
                Window!.InsetsController!.SystemBarsBehavior = (int)WindowInsetsControllerBehavior.ShowTransientBarsBySwipe;
                return;
            }

            if (Window is not null)
            {
                Window.DecorView.SystemUiVisibility = (StatusBarVisibility)(
                    SystemUiFlags.Fullscreen |
                    SystemUiFlags.HideNavigation |
                    SystemUiFlags.ImmersiveSticky |
                    SystemUiFlags.LayoutFullscreen |
                    SystemUiFlags.LayoutHideNavigation |
                    SystemUiFlags.LayoutStable);
            }
        }
    }
}
