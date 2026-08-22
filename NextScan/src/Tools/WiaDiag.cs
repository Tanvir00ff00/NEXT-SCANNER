// Diagnostic harness: isolates where WIA property reads fail.
// Not shipped - build with -define:WIADIAG.
using System;
using System.Runtime.InteropServices;
using NextScan.Wia;

namespace NextScan.Tools
{
    public static class WiaDiag
    {
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        delegate int ReadMultipleFn(IntPtr self, uint cpspec, IntPtr rgpspec, IntPtr rgpropvar);

        [MTAThread]
        public static int Main(string[] args)
        {
            Console.WriteLine("IntPtr.Size            = " + IntPtr.Size);
            Console.WriteLine("PropSpecSize           = " + PropBuf.PropSpecSize);
            Console.WriteLine("PropVariantSize        = " + PropBuf.PropVariantSize);
            Console.WriteLine("Marshal.SizeOf PROPSPEC   = " + Marshal.SizeOf(typeof(PROPSPEC)));
            Console.WriteLine("Marshal.SizeOf PROPVARIANT= " + Marshal.SizeOf(typeof(PROPVARIANT)));

            Type t = Type.GetTypeFromCLSID(WiaConst.CLSID_WiaDevMgr2, false);
            object mgrObj = Activator.CreateInstance(t);
            Console.WriteLine("devmgr created: " + (mgrObj != null));

            IWiaDevMgr2 mgr = mgrObj as IWiaDevMgr2;
            Console.WriteLine("cast to IWiaDevMgr2: " + (mgr != null));

            IEnumWIA_DEV_INFO en;
            int hr = mgr.EnumDeviceInfo(0, out en);
            Console.WriteLine("EnumDeviceInfo hr=0x" + hr.ToString("x8") + " enum=" + (en != null));
            if (en == null) return 1;

            uint count;
            hr = en.GetCount(out count);
            Console.WriteLine("GetCount hr=0x" + hr.ToString("x8") + " count=" + count);

            IWiaPropertyStorage storage;
            uint fetched;
            hr = en.Next(1, out storage, out fetched);
            Console.WriteLine("Next hr=0x" + hr.ToString("x8") + " fetched=" + fetched + " storage=" + (storage != null));
            if (storage == null) return 1;

            // Does the object actually expose the IID we declared?
            IntPtr unk = Marshal.GetIUnknownForObject(storage);
            Console.WriteLine("IUnknown ptr = 0x" + unk.ToInt64().ToString("x"));

            Guid iid = new Guid("98B5E8A0-29CC-491a-AAC0-E6DB4FDCCEB6");
            IntPtr ps = IntPtr.Zero;
            int qhr = Marshal.QueryInterface(unk, ref iid, out ps);
            Console.WriteLine("QI IWiaPropertyStorage hr=0x" + qhr.ToString("x8") + " ptr=0x" + ps.ToInt64().ToString("x"));

            if (ps == IntPtr.Zero) { Console.WriteLine("=> object does NOT implement the declared IID"); return 1; }

            // Manual vtable call, bypassing all CLR interface marshalling.
            IntPtr vtbl = Marshal.ReadIntPtr(ps, 0);
            for (int i = 0; i < 20; i++)
                Console.WriteLine("  slot[" + i + "] = 0x" + Marshal.ReadIntPtr(vtbl, i * IntPtr.Size).ToInt64().ToString("x"));

            string test = (args.Length > 0) ? args[0] : "getcount";

            if (test == "getcount")
            {
                // Discriminator: a no-array method on the same vtable. If this works,
                // the object and slot mapping are fine and the fault is argument-side.
                uint n;
                int ghr = storage.GetCount(out n);
                Console.WriteLine("GetCount hr=0x" + ghr.ToString("x8") + " props=" + n);
            }
            else if (test == "read")
            {
                IntPtr slot3 = Marshal.ReadIntPtr(vtbl, 3 * IntPtr.Size);
                ReadMultipleFn fn = (ReadMultipleFn)Marshal.GetDelegateForFunctionPointer(slot3, typeof(ReadMultipleFn));
                // Oversized, zeroed buffers rule out a buffer-length miscalculation.
                IntPtr spec = Marshal.AllocCoTaskMem(256);
                IntPtr var = Marshal.AllocCoTaskMem(256);
                PropBuf.Zero(spec, 256); PropBuf.Zero(var, 256);
                Marshal.WriteInt32(spec, 0, 0);
                Marshal.WriteInt32(spec, IntPtr.Size, (int)WiaConst.WIA_DIP_DEV_ID);
                Console.WriteLine("calling ReadMultiple with 256-byte buffers...");
                int rhr = fn(ps, 1, spec, var);
                Console.WriteLine("hr=0x" + rhr.ToString("x8") + " vt=" + PropBuf.GetVt(var));
            }
            return 0;
        }
    }
}
