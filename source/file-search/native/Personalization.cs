// Purpose: local branding, avatar normalization and login startup registration.
// Dependencies: Windows/.NET 8 WinForms. Outputs: data/profile.png and an HKCU Run entry.
// Build: native/build.ps1. Check: "AI Center.exe" --check-personalization --data NEW_FOLDER.
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Text.Json;
using Microsoft.Win32;

namespace LocalAICenter;

internal static class Personalization
{
    internal static readonly Icon AppIcon=LoadIcon();
    static Icon LoadIcon()
    {
        using var stream=typeof(Personalization).Assembly.GetManifestResourceStream("LocalAICenter.App.ico")!;
        using var icon=new Icon(stream);return (Icon)icon.Clone();
    }
    internal static string StartupCommand(string root)=>"\""+Path.Combine(root,"AI Center.exe")+"\" --tray";
    internal static void Startup(bool enabled)
    {
        // Development previews and isolated checks must never take over login startup.
        if(!File.Exists(Path.Combine(Program.Root,"package-manifest.json")) || XiaomiRevamp.Suite.SuiteEnvironment.Portable)return;
        using var key=Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
        // An installation that predates the suite left a second entry for this same program.
        if(XiaomiRevamp.Suite.SuiteEnvironment.Enabled&&key.GetValue("LocalAICenter") is string legacy&&legacy==StartupCommand(Program.Root))key.DeleteValue("LocalAICenter",false);
        if(enabled)key.SetValue(XiaomiRevamp.Suite.SuiteEnvironment.Enabled ? "XiaomiRevampSuite.Search." + XiaomiRevamp.Suite.SuiteEnvironment.Identity : "LocalAICenter",StartupCommand(Program.Root));
        else if(key.GetValue(XiaomiRevamp.Suite.SuiteEnvironment.Enabled ? "XiaomiRevampSuite.Search." + XiaomiRevamp.Suite.SuiteEnvironment.Identity : "LocalAICenter") is string command&&command==StartupCommand(Program.Root))key.DeleteValue(XiaomiRevamp.Suite.SuiteEnvironment.Enabled ? "XiaomiRevampSuite.Search." + XiaomiRevamp.Suite.SuiteEnvironment.Identity : "LocalAICenter",false);
    }
    internal static string? Picture(string data)
    {
        string path=Path.Combine(data,"profile.png");
        if(!File.Exists(path))return null;
        if(new FileInfo(path).Length>4*1024*1024)throw new InvalidDataException("Profile picture is too large.");
        return "data:image/png;base64,"+Convert.ToBase64String(File.ReadAllBytes(path));
    }
    internal static void SavePicture(string input,string data)
    {
        if(new FileInfo(input).Length>10*1024*1024)throw new InvalidDataException("Choose an image smaller than 10 MB.");
        using var source=Image.FromFile(input);
        if(source.Width>8192||source.Height>8192||(long)source.Width*source.Height>32000000)throw new InvalidDataException("Choose an image no larger than 8192 pixels and 32 megapixels.");
        using var cropped=new Bitmap(256,256);using var graphics=Graphics.FromImage(cropped);
        graphics.InterpolationMode=InterpolationMode.HighQualityBicubic;
        // Crop the centered square into a fixed-size local copy. Original photos stay untouched.
        int side=Math.Min(source.Width,source.Height);
        graphics.DrawImage(source,new Rectangle(0,0,256,256),(source.Width-side)/2,(source.Height-side)/2,side,side,GraphicsUnit.Pixel);
        string path=Path.Combine(data,"profile.png"),pending=path+".pending";
        cropped.Save(pending,ImageFormat.Png);File.Move(pending,path,true);
    }
    internal static object Check(string data)
    {
        string source=Path.Combine(data,"picture-fixture.png");
        using(var bitmap=new Bitmap(400,200)){using var graphics=Graphics.FromImage(bitmap);graphics.Clear(Color.MediumPurple);bitmap.Save(source,ImageFormat.Png);}
        byte[] original=File.ReadAllBytes(source);SavePicture(source,data);
        using var picture=Image.FromFile(Path.Combine(data,"profile.png"));
        if(picture.Width!=256||picture.Height!=256||!File.ReadAllBytes(source).SequenceEqual(original)||!Picture(data)!.StartsWith("data:image/png;base64,"))throw new InvalidOperationException("Profile picture check failed.");
        if(!StartupCommand(@"C:\Program Files\Xiaomi Revamp\AI Center").Equals("\"C:\\Program Files\\Xiaomi Revamp\\AI Center\\AI Center.exe\" --tray"))throw new InvalidOperationException("Startup quoting failed.");
        ControlTap.Check();
        return new {passed=true,avatarWidth=picture.Width,avatarHeight=picture.Height,originalUntouched=true,localDataUrl=true,iconWidth=AppIcon.Width,startupQuoted=true,doubleCtrl=true,chordsIgnored=true};
    }
}

internal sealed class ControlTap
{
    readonly HashSet<uint> pressed=new();readonly int interval;
    long down,last;bool chord;
    internal ControlTap(int interval){this.interval=interval;}
    internal bool Key(uint key,bool isDown,long now)
    {
        bool control=key is 0x11 or 0xA2 or 0xA3;
        if(isDown&&pressed.Add(key))
        {
            if(control){down=now;chord=pressed.Any(value=>value is not (0x11 or 0xA2 or 0xA3));}
            else{last=0;if(pressed.Any(value=>value is 0x11 or 0xA2 or 0xA3))chord=true;}
        }
        if(!isDown&&pressed.Remove(key)&&control&&!pressed.Any(value=>value is 0x11 or 0xA2 or 0xA3))
        {
            if(!chord&&now-down<600){if(last>0&&now-last<=interval){last=0;return true;}last=now;}else last=0;
        }
        return false;
    }
    internal static void Check()
    {
        var gesture=new ControlTap(450);
        if(gesture.Key(0xA2,true,100)||gesture.Key(0xA2,true,110)||gesture.Key(0xA2,false,150)||gesture.Key(0xA2,true,300)||!gesture.Key(0xA2,false,340))throw new InvalidOperationException("Double Ctrl failed.");
        if(gesture.Key(0xA2,true,1000)||gesture.Key(0x43,true,1010)||gesture.Key(0x43,false,1020)||gesture.Key(0xA2,false,1030)||gesture.Key(0xA2,true,1100)||gesture.Key(0xA2,false,1130))throw new InvalidOperationException("Ctrl chord triggered search.");
        gesture.Key(0x58,true,1200);gesture.Key(0x58,false,1230);gesture.Key(0xA2,true,1250);
        if(gesture.Key(0xA2,false,1280))throw new InvalidOperationException("Intervening text triggered search.");
        gesture.Key(0xA3,true,2000);if(gesture.Key(0xA3,false,2050))throw new InvalidOperationException("Slow tap triggered search.");
    }
}
