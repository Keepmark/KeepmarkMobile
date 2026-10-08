using Android.App;
using Android.Content;
using Android.OS;

namespace KeepmarkMobile.Platforms.Android;

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

        // Fast path when warm; MainActivity re-handles the same payload below,
        // so a cold start (no services yet here) loses nothing.
        var forward = new Intent(this, typeof(MainActivity));
        forward.SetAction(Intent.ActionSend);
        forward.SetType("text/plain");
        forward.PutExtra(Intent.ExtraText, Intent.GetStringExtra(Intent.ExtraText));
        var subject = Intent.GetStringExtra(Intent.ExtraSubject);
        if (subject is not null)
        {
            forward.PutExtra(Intent.ExtraSubject, subject);
        }

        forward.PutExtra("xbs_shared", true);
        forward.AddFlags(ActivityFlags.ClearTop | ActivityFlags.SingleTop | ActivityFlags.NewTask);
        StartActivity(forward);
        Finish();
    }
}
