using Android.App;
using Android.Content;
using Android.OS;

namespace KeepmarkMobile.Platforms.Android;

// Translucent target for tapped keepmark://sync links. The link is handed to
// the intake (in memory only) and forwarded to MainActivity as shared text, so
// cold and warm starts take the same path as ShareActivity.

[Activity(Theme = "@android:style/Theme.Translucent.NoTitleBar", NoHistory = true, Exported = true, ExcludeFromRecents = true)]
[IntentFilter([Intent.ActionView], Categories = [Intent.CategoryDefault, Intent.CategoryBrowsable],
    DataScheme = Keepmark.Core.Workflow.SyncShareLink.Scheme, DataHost = Keepmark.Core.Workflow.SyncShareLink.Host)]
public class SyncLinkActivity : Activity
{
    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        ShareIntake.Handle(Intent);

        var forward = new Intent(this, typeof(MainActivity));
        forward.SetAction(Intent.ActionSend);
        forward.SetType("text/plain");
        forward.PutExtra(Intent.ExtraText, Intent?.DataString);
        forward.AddFlags(ActivityFlags.ClearTop | ActivityFlags.SingleTop | ActivityFlags.NewTask);
        StartActivity(forward);
        Finish();
    }
}
