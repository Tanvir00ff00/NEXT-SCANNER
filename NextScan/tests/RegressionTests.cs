using System;
using System.IO;
using System.Drawing;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using NextScan.Core;
using NextScan.App;
using NextScan.Net;

class RegressionTests
{
    static int failed, passed;
    static void Check(string name, Action test)
    {
        try { test(); passed++; Console.WriteLine("  ok   " + name); }
        catch (Exception e) { failed++; Console.WriteLine("  FAIL " + name + ": " + e.Message); }
    }
    static void Require(bool condition) { if (!condition) throw new Exception("Assertion failed"); }
    static bool Equal(byte[] a, byte[] b)
    {
        if(a.Length != b.Length) return false;
        for(int i=0;i<a.Length;i++) if(a[i]!=b[i]) return false;
        return true;
    }
    public static int Main(string[] args)
    {
        string output = args[0]; Directory.CreateDirectory(output);
        Check("broker_leaves_other_process_hosts_alive", delegate {
            string fake = Path.Combine(output,"NextScan.Host32.exe");
            System.Diagnostics.ProcessStartInfo info = new System.Diagnostics.ProcessStartInfo(fake,"wait");
            info.UseShellExecute=false; info.CreateNoWindow=true;
            using(System.Diagnostics.Process sentinel=System.Diagnostics.Process.Start(info))
            {
                try
                {
                    DeviceBroker broker=new DeviceBroker {HostDirectory=output};
                    MethodInfo run=typeof(DeviceBroker).GetMethod("RunHost",BindingFlags.NonPublic|BindingFlags.Instance);
                    run.Invoke(broker,new object[]{fake,"",5000,null});
                    Require(!sentinel.HasExited);
                }
                finally {if(!sentinel.HasExited) {sentinel.Kill();sentinel.WaitForExit();}}
            }
        });
        foreach (int depth in new[] {1,8,16})
        foreach (int channels in new[] {1,3})
        {
            if(depth==1 && channels==3) continue;
            int d=depth, c=channels;
            Check("rotation_all_transforms_"+d+"bit_"+c+"ch", delegate {
                RawImage raw=new RawImage { Width=9, Height=3, Channels=c, BitsPerChannel=d, XDpi=150,YDpi=300,PageIndex=7,Side=1 };
                raw.Stride=(raw.Width*raw.BitsPerPixel+7)/8;
                raw.Pixels=new byte[raw.Stride*raw.Height];
                for(int i=0;i<raw.Pixels.Length;i++) raw.Pixels[i]=(byte)(i*37+3);
                if(d==1) for(int y=0;y<raw.Height;y++) raw.Pixels[y*raw.Stride+1]&=128;
                using(Bitmap original=raw.ToBitmap())
                for(int t=0;t<8;t++)
                {
                    RawImage result=raw.Rotate((RotateFlipType)t);
                    Require(result.IsValid && result.BitsPerChannel==d && result.Channels==c && result.PageIndex==7 && result.Side==1);
                    Require(result.XDpi==((t&1)==1?300:150));
                    using(Bitmap expected=(Bitmap)original.Clone())
                    using(Bitmap actual=result.ToBitmap())
                    {
                        expected.RotateFlip((RotateFlipType)t);
                        Require(expected.Size==actual.Size);
                        for(int y=0;y<actual.Height;y++) for(int x=0;x<actual.Width;x++)
                            Require(expected.GetPixel(x,y)==actual.GetPixel(x,y));
                    }
                    RawImage restored=result.Rotate((RotateFlipType)(t>=4?t:((4-t)&3)));
                    Require(Equal(raw.Pixels,restored.Pixels));
                }
            });
        }
        Check("invalid_raster_rejected",delegate {
            RawImage raw=new RawImage {Width=10,Height=2,Stride=1,Channels=3,BitsPerChannel=16,Pixels=new byte[2]};
            Require(!raw.IsValid && raw.ToBitmap()==null);
            raw.Width=1;raw.Stride=1;raw.Channels=2;raw.BitsPerChannel=8;
            Require(!raw.IsValid);
            raw.Width=int.MaxValue;raw.Channels=3;raw.BitsPerChannel=16;
            Require(!raw.IsValid);
        });
        RawImage page=new RawImage {Width=2,Height=1,Stride=6,Channels=3,BitsPerChannel=8,Pixels=new byte[]{0,0,255,0,255,0}};
        Check("pdf_failed_export_preserves_existing",delegate {
            string path=Path.Combine(output,"preserve.pdf");File.WriteAllText(path,"original");
            Require(!StudioExport.SavePdf(new List<RawImage>{page,null},path));
            Require(File.ReadAllText(path)=="original");
        });
        Check("pdf_creates_directory_and_replaces_complete_file",delegate {
            string path=Path.Combine(output,"nested","pages.pdf");
            Require(StudioExport.SavePdf(new List<RawImage>{page,page},path));
            Require(StudioExport.SavePdf(new List<RawImage>{page,page},path));
            byte[] data=File.ReadAllBytes(path);
            string text=Encoding.ASCII.GetString(data);
            Require(data[10]==0xE2 && text.Contains("/Count 2") && text.EndsWith("%%EOF\n"));
            Require(Directory.GetFiles(Path.GetDirectoryName(path),"*.tmp").Length==0);
        });
        Check("unknown_format_does_not_write_jpeg",delegate {
            string path=Path.Combine(output,"unknown.xyz");
            using(Bitmap bitmap=page.ToBitmap()) Require(!StudioExport.SaveBitmap(bitmap,path,"xyz"));
            Require(!File.Exists(path));
        });
        Check("bitmap_failure_preserves_destination",delegate {
            string path=Path.Combine(output,"preserve.png");File.WriteAllText(path,"original");
            using(Bitmap bitmap=page.ToBitmap())
            {
                Require(!StudioExport.SaveBitmap(bitmap,path,"invalid"));
                Require(File.ReadAllText(path)=="original");
                Require(StudioExport.SaveBitmap(bitmap,path,"png"));
            }
            using(Bitmap loaded=new Bitmap(path)) Require(loaded.GetPixel(0,0).R==255);
        });
        Check("escl_ipv6_and_scheme_validation",delegate {
            string ipv6 = new EsclClient("http://[::1]:8951/custom/").BaseUrl; Require(new Uri(ipv6) == new Uri("http://[::1]:8951/custom"));
            bool rejected=false;try { new EsclClient("ftp://host/eSCL"); } catch(ArgumentException) {rejected=true;}
            Require(rejected);
        });
        Check("escl_chunked_body_and_truncation",delegate {
            MethodInfo method=typeof(EsclClient).GetMethod("DecodeChunks",BindingFlags.NonPublic|BindingFlags.Static);
            byte[] actual=(byte[])method.Invoke(null,new object[]{Encoding.ASCII.GetBytes("3;ext=1\r\nabc\r\n2\r\nde\r\n0\r\n\r\n")});
            Require(Encoding.ASCII.GetString(actual)=="abcde");
            bool rejected=false;
            try {method.Invoke(null,new object[]{Encoding.ASCII.GetBytes("5\r\nabc")});}
            catch(TargetInvocationException e) {rejected=e.InnerException is IOException;}
            Require(rejected);
        });
        Console.WriteLine(passed+" passed, "+failed+" failed");return failed==0?0:1;
    }
}


