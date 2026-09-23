using Android.App;
using Android.Content;
using Android.OS;

namespace XBrowserSyncNg.Platforms.Android;

// Dedicated translucent entry point for ACTION_SEND. A separate activity avoids
// all SingleTop/task-affinity delivery quirks on the main activity: the share
// always lands here, we stash it, then forward to MainActivity.

[Activity(Theme = "@android:style/Theme.Translucent.NoTitleBar", NoHistory = true, Exported = true)]
[IntentFilter([Intent.ActionSend], Categories = [Intent.CategoryDefault], DataMimeType = "text/plain")]
public class ShareActivity : Activity
{
    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        ShareIntake.Handle(Intent);

        var forward = new Intent(this, typeof(MainActivity));
        forward.SetAction(Intent.ActionSend);
        forward.PutExtra("xbs_shared", true);
        forward.AddFlags(ActivityFlags.ClearTop | ActivityFlags.SingleTop | ActivityFlags.NewTask);
        StartActivity(forward);
        Finish();
    }
}
