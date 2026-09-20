using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace NextScan.Core
{
    /// <summary>
    /// Just enough of the ONNX Runtime C API to run a model, bound by hand.
    ///
    /// Why by hand
    /// -----------
    /// This project builds with csc directly and takes no package
    /// dependencies, so the usual managed wrapper is not available. What is
    /// available is the same thing that wrapper talks to: a single exported
    /// function, `OrtGetApiBase`, which hands back a table of function
    /// pointers. Binding the dozen entries needed for inference is a page of
    /// interop, and it keeps the build exactly as it was -- one compiler, no
    /// restore step, and a native DLL that is simply present or absent.
    ///
    /// The table is ordered, not named, so the indices below are part of the
    /// contract. They are read off `onnxruntime_c_api.h` for API version 20
    /// (runtime 1.20.x) and verified at load time by `SelfTest`, which runs a
    /// known tensor through and checks what comes back. Entries are only ever
    /// appended to the end of that table between versions, so a newer runtime
    /// stays compatible; an older one will fail the self test and be refused
    /// rather than crash.
    ///
    /// Absent by design
    /// ----------------
    /// Nothing here throws on a missing DLL. `Available` reports what happened
    /// and every caller is expected to carry on without it -- the detector this
    /// serves has to work on a machine where the model was never installed.
    /// </summary>
    internal static class Ort
    {
        const int ApiVersion = 20;

        // Indices into the OrtApi function table, from onnxruntime_c_api.h.
        const int IdxGetErrorMessage = 2;
        const int IdxCreateEnv = 3;
        const int IdxCreateSession = 7;
        const int IdxRun = 9;
        const int IdxCreateSessionOptions = 10;
        const int IdxSetGraphOptimizationLevel = 23;
        const int IdxSetIntraOpNumThreads = 24;
        const int IdxSessionGetInputCount = 30;
        const int IdxSessionGetOutputCount = 31;
        const int IdxSessionGetInputName = 36;
        const int IdxSessionGetOutputName = 37;
        const int IdxCreateTensorWithData = 49;
        const int IdxGetTensorMutableData = 51;
        const int IdxGetDimensionsCount = 61;
        const int IdxGetDimensions = 62;
        const int IdxGetTensorTypeAndShape = 65;
        const int IdxCreateCpuMemoryInfo = 69;
        const int IdxAllocatorFree = 76;
        const int IdxGetDefaultAllocator = 78;
        const int IdxReleaseEnv = 92;
        const int IdxReleaseStatus = 93;
        const int IdxReleaseMemoryInfo = 94;
        const int IdxReleaseSession = 95;
        const int IdxReleaseValue = 96;
        const int IdxReleaseShapeInfo = 99;
        const int IdxReleaseSessionOptions = 100;

        const int TensorElementFloat = 1;

        [DllImport("onnxruntime.dll", CallingConvention = CallingConvention.StdCall)]
        static extern IntPtr OrtGetApiBase();

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        delegate IntPtr DGetApi(uint version);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        delegate IntPtr DErrorMessage(IntPtr status);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        delegate IntPtr DCreateEnv(int level, [MarshalAs(UnmanagedType.LPStr)] string id, out IntPtr env);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        delegate IntPtr DCreateSessionOptions(out IntPtr options);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        delegate IntPtr DSetInt(IntPtr options, int value);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        delegate IntPtr DCreateSession(IntPtr env, [MarshalAs(UnmanagedType.LPWStr)] string path,
                                       IntPtr options, out IntPtr session);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        delegate IntPtr DCreateCpuMemoryInfo(int allocator, int memory, out IntPtr info);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        delegate IntPtr DCreateTensor(IntPtr info, IntPtr data, UIntPtr dataLength,
                                      long[] shape, UIntPtr shapeLength, int type, out IntPtr value);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        delegate IntPtr DRun(IntPtr session, IntPtr runOptions, IntPtr[] inputNames, IntPtr[] inputs,
                             UIntPtr inputCount, IntPtr[] outputNames, UIntPtr outputCount, IntPtr[] outputs);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        delegate IntPtr DGetData(IntPtr value, out IntPtr data);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        delegate IntPtr DGetShapeInfo(IntPtr value, out IntPtr info);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        delegate IntPtr DGetDimCount(IntPtr info, out UIntPtr count);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        delegate IntPtr DGetDims(IntPtr info, long[] dims, UIntPtr count);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        delegate IntPtr DGetCount(IntPtr session, out UIntPtr count);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        delegate IntPtr DGetName(IntPtr session, UIntPtr index, IntPtr allocator, out IntPtr name);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        delegate IntPtr DGetAllocator(out IntPtr allocator);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        delegate IntPtr DAllocatorFree(IntPtr allocator, IntPtr memory);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        delegate void DRelease(IntPtr handle);

        static readonly object Gate = new object();
        static bool _probed;
        static string _why = "not probed";
        static IntPtr _api = IntPtr.Zero;
        static IntPtr _env = IntPtr.Zero;
        static IntPtr _allocator = IntPtr.Zero;

        static DErrorMessage _errorMessage;
        static DCreateSessionOptions _createOptions;
        static DSetInt _setThreads, _setOptimization;
        static DCreateSession _createSession;
        static DCreateCpuMemoryInfo _createMemoryInfo;
        static DCreateTensor _createTensor;
        static DRun _run;
        static DGetData _getData;
        static DGetShapeInfo _getShapeInfo;
        static DGetDimCount _getDimCount;
        static DGetDims _getDims;
        static DGetCount _inputCount, _outputCount;
        static DGetName _inputName, _outputName;
        static DAllocatorFree _allocatorFree;
        static DRelease _releaseValue, _releaseSession, _releaseOptions, _releaseMemoryInfo, _releaseShapeInfo, _releaseStatus;

        /// <summary>Why the runtime is or is not usable. Never throws.</summary>
        internal static string Availability { get { Probe(); return _why; } }

        internal static bool Available { get { Probe(); return _api != IntPtr.Zero; } }

        static void Probe()
        {
            lock (Gate)
            {
                if (_probed) return;
                _probed = true;
                try
                {
                    IntPtr baseTable = OrtGetApiBase();
                    if (baseTable == IntPtr.Zero) { _why = "OrtGetApiBase returned nothing"; return; }

                    DGetApi getApi = (DGetApi)Marshal.GetDelegateForFunctionPointer(
                        Marshal.ReadIntPtr(baseTable), typeof(DGetApi));
                    IntPtr api = getApi(ApiVersion);
                    if (api == IntPtr.Zero) { _why = "runtime is older than API version " + ApiVersion; return; }

                    _errorMessage = Bind<DErrorMessage>(api, IdxGetErrorMessage);
                    _createOptions = Bind<DCreateSessionOptions>(api, IdxCreateSessionOptions);
                    _setThreads = Bind<DSetInt>(api, IdxSetIntraOpNumThreads);
                    _setOptimization = Bind<DSetInt>(api, IdxSetGraphOptimizationLevel);
                    _createSession = Bind<DCreateSession>(api, IdxCreateSession);
                    _createMemoryInfo = Bind<DCreateCpuMemoryInfo>(api, IdxCreateCpuMemoryInfo);
                    _createTensor = Bind<DCreateTensor>(api, IdxCreateTensorWithData);
                    _run = Bind<DRun>(api, IdxRun);
                    _getData = Bind<DGetData>(api, IdxGetTensorMutableData);
                    _getShapeInfo = Bind<DGetShapeInfo>(api, IdxGetTensorTypeAndShape);
                    _getDimCount = Bind<DGetDimCount>(api, IdxGetDimensionsCount);
                    _getDims = Bind<DGetDims>(api, IdxGetDimensions);
                    _inputCount = Bind<DGetCount>(api, IdxSessionGetInputCount);
                    _outputCount = Bind<DGetCount>(api, IdxSessionGetOutputCount);
                    _inputName = Bind<DGetName>(api, IdxSessionGetInputName);
                    _outputName = Bind<DGetName>(api, IdxSessionGetOutputName);
                    _allocatorFree = Bind<DAllocatorFree>(api, IdxAllocatorFree);
                    _releaseValue = Bind<DRelease>(api, IdxReleaseValue);
                    _releaseSession = Bind<DRelease>(api, IdxReleaseSession);
                    _releaseOptions = Bind<DRelease>(api, IdxReleaseSessionOptions);
                    _releaseMemoryInfo = Bind<DRelease>(api, IdxReleaseMemoryInfo);
                    _releaseShapeInfo = Bind<DRelease>(api, IdxReleaseShapeInfo);
                    _releaseStatus = Bind<DRelease>(api, IdxReleaseStatus);

                    DCreateEnv createEnv = Bind<DCreateEnv>(api, IdxCreateEnv);
                    IntPtr env;
                    IntPtr status = createEnv(3 /* warning */, "NextScan", out env);
                    if (status != IntPtr.Zero) { _why = Consume(status); return; }

                    DGetAllocator getAllocator = Bind<DGetAllocator>(api, IdxGetDefaultAllocator);
                    IntPtr allocator;
                    status = getAllocator(out allocator);
                    if (status != IntPtr.Zero) { _why = Consume(status); return; }

                    _api = api; _env = env; _allocator = allocator;
                    _why = "ready";
                }
                catch (DllNotFoundException) { _why = "onnxruntime.dll is not installed beside the application"; }
                catch (EntryPointNotFoundException) { _why = "onnxruntime.dll is present but is not the ONNX Runtime"; }
                catch (BadImageFormatException) { _why = "onnxruntime.dll is built for the wrong architecture"; }
                catch (Exception ex) { _why = "ONNX Runtime could not be started: " + ex.Message; }
            }
        }

        static T Bind<T>(IntPtr api, int index) where T : class
        {
            IntPtr slot = Marshal.ReadIntPtr(api, index * IntPtr.Size);
            if (slot == IntPtr.Zero) throw new InvalidOperationException("empty slot " + index);
            return (T)(object)Marshal.GetDelegateForFunctionPointer(slot, typeof(T));
        }

        /// <summary>Reads an error out of a status and frees it.</summary>
        static string Consume(IntPtr status)
        {
            try
            {
                IntPtr text = _errorMessage(status);
                return text == IntPtr.Zero ? "unknown error" : Marshal.PtrToStringAnsi(text);
            }
            finally { _releaseStatus(status); }
        }

        /// <summary>
        /// A loaded model. Disposing it releases the native session; a session
        /// that failed to load reports why through <see cref="Error"/> and
        /// runs nothing.
        /// </summary>
        internal sealed class Session : IDisposable
        {
            IntPtr _session, _options, _memory;
            readonly List<IntPtr> _inputNames = new List<IntPtr>();
            readonly List<IntPtr> _outputNames = new List<IntPtr>();

            internal string Error { get; private set; }
            internal bool Loaded { get { return _session != IntPtr.Zero; } }
            internal string[] Inputs { get; private set; }
            internal string[] Outputs { get; private set; }

            internal Session(string modelPath, int threads)
            {
                if (!Available) { Error = Availability; return; }
                try
                {
                    IntPtr status = _createOptions(out _options);
                    if (status != IntPtr.Zero) { Error = Consume(status); return; }
                    _setThreads(_options, threads < 1 ? 1 : threads);
                    _setOptimization(_options, 99 /* all */);

                    status = _createSession(_env, modelPath, _options, out _session);
                    if (status != IntPtr.Zero) { Error = Consume(status); return; }

                    status = _createMemoryInfo(1 /* arena */, 0 /* cpu */, out _memory);
                    if (status != IntPtr.Zero) { Error = Consume(status); return; }

                    Inputs = Names(true, _inputNames);
                    Outputs = Names(false, _outputNames);
                }
                catch (Exception ex) { Error = ex.Message; }
            }

            string[] Names(bool inputs, List<IntPtr> store)
            {
                UIntPtr count;
                IntPtr status = inputs ? _inputCount(_session, out count) : _outputCount(_session, out count);
                if (status != IntPtr.Zero) { Error = Consume(status); return new string[0]; }

                string[] names = new string[(int)count];
                for (int i = 0; i < names.Length; i++)
                {
                    IntPtr name;
                    status = inputs ? _inputName(_session, (UIntPtr)i, _allocator, out name)
                                    : _outputName(_session, (UIntPtr)i, _allocator, out name);
                    if (status != IntPtr.Zero) { Error = Consume(status); return names; }
                    names[i] = Marshal.PtrToStringAnsi(name);
                    // The runtime allocated this; keep the pointer so Run can
                    // hand the very same bytes back without re-marshalling.
                    store.Add(name);
                }
                return names;
            }

            /// <summary>
            /// Runs the model. Inputs are given in the model's own input order,
            /// each as a flat array with its shape. Outputs come back the same
            /// way. Returns null and sets <see cref="Error"/> on failure.
            /// </summary>
            internal float[][] Run(float[][] values, long[][] shapes, out long[][] outputShapes)
            {
                return Run(values, shapes, null, out outputShapes);
            }

            /// <summary>
            /// Runs the model asking only for <paramref name="wanted"/> outputs,
            /// by index. Everything a caller does not ask for is still computed
            /// by the graph, but never crosses back into managed memory -- and
            /// on a decoder whose full-size mask is three megabytes a call, that
            /// copy is most of what the call costs on this side.
            /// </summary>
            internal float[][] Run(float[][] values, long[][] shapes, int[] wanted, out long[][] outputShapes)
            {
                outputShapes = null;
                if (!Loaded) return null;

                IntPtr[] askFor;
                if (wanted == null) askFor = _outputNames.ToArray();
                else
                {
                    askFor = new IntPtr[wanted.Length];
                    for (int i = 0; i < wanted.Length; i++) askFor[i] = _outputNames[wanted[i]];
                }

                GCHandle[] pinned = new GCHandle[values.Length];
                IntPtr[] tensors = new IntPtr[values.Length];
                IntPtr[] results = new IntPtr[askFor.Length];
                try
                {
                    for (int i = 0; i < values.Length; i++)
                    {
                        pinned[i] = GCHandle.Alloc(values[i], GCHandleType.Pinned);
                        IntPtr status = _createTensor(_memory, pinned[i].AddrOfPinnedObject(),
                                                      (UIntPtr)(values[i].Length * sizeof(float)),
                                                      shapes[i], (UIntPtr)shapes[i].Length,
                                                      TensorElementFloat, out tensors[i]);
                        if (status != IntPtr.Zero) { Error = Consume(status); return null; }
                    }

                    IntPtr runStatus = _run(_session, IntPtr.Zero, _inputNames.ToArray(), tensors,
                                            (UIntPtr)values.Length, askFor,
                                            (UIntPtr)askFor.Length, results);
                    if (runStatus != IntPtr.Zero) { Error = Consume(runStatus); return null; }

                    float[][] outputs = new float[results.Length][];
                    outputShapes = new long[results.Length][];
                    for (int i = 0; i < results.Length; i++)
                    {
                        long[] shape = ShapeOf(results[i]);
                        if (shape == null) return null;
                        outputShapes[i] = shape;

                        long total = 1;
                        foreach (long d in shape) total *= d < 0 ? 0 : d;

                        IntPtr data;
                        IntPtr status = _getData(results[i], out data);
                        if (status != IntPtr.Zero) { Error = Consume(status); return null; }

                        outputs[i] = new float[total];
                        if (total > 0) Marshal.Copy(data, outputs[i], 0, (int)total);
                    }
                    return outputs;
                }
                catch (Exception ex) { Error = ex.Message; return null; }
                finally
                {
                    foreach (IntPtr t in tensors) if (t != IntPtr.Zero) _releaseValue(t);
                    foreach (IntPtr r in results) if (r != IntPtr.Zero) _releaseValue(r);
                    for (int i = 0; i < pinned.Length; i++) if (pinned[i].IsAllocated) pinned[i].Free();
                }
            }

            long[] ShapeOf(IntPtr value)
            {
                IntPtr info;
                IntPtr status = _getShapeInfo(value, out info);
                if (status != IntPtr.Zero) { Error = Consume(status); return null; }
                try
                {
                    UIntPtr count;
                    status = _getDimCount(info, out count);
                    if (status != IntPtr.Zero) { Error = Consume(status); return null; }

                    long[] dims = new long[(int)count];
                    status = _getDims(info, dims, count);
                    if (status != IntPtr.Zero) { Error = Consume(status); return null; }
                    return dims;
                }
                finally { _releaseShapeInfo(info); }
            }

            public void Dispose()
            {
                foreach (IntPtr name in _inputNames) if (name != IntPtr.Zero) _allocatorFree(_allocator, name);
                foreach (IntPtr name in _outputNames) if (name != IntPtr.Zero) _allocatorFree(_allocator, name);
                _inputNames.Clear(); _outputNames.Clear();
                if (_memory != IntPtr.Zero) { _releaseMemoryInfo(_memory); _memory = IntPtr.Zero; }
                if (_session != IntPtr.Zero) { _releaseSession(_session); _session = IntPtr.Zero; }
                if (_options != IntPtr.Zero) { _releaseOptions(_options); _options = IntPtr.Zero; }
            }
        }
    }
}
