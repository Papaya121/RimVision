using System;
using System.Runtime.InteropServices;
using UnityEngine;

namespace Recovery.Rim3D
{
    internal static class MacMouse
    {
        [StructLayout(LayoutKind.Sequential)]
        struct Block
        {
            public IntPtr Isa;
            public int Flags, Reserved;
            public IntPtr Invoke, Descriptor;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct BlockDescriptor
        {
            public UIntPtr Reserved, Size;
            public IntPtr Copy, Dispose, Signature, Layout;
        }

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        delegate IntPtr MouseEventHandler(IntPtr block, IntPtr mouseEvent);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        delegate void BlockCopyHandler(IntPtr destination, IntPtr source);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        delegate void BlockDisposeHandler(IntPtr block);

        [DllImport("/usr/lib/libobjc.A.dylib")] static extern IntPtr objc_getClass(string name);
        [DllImport("/usr/lib/libobjc.A.dylib")] static extern IntPtr sel_registerName(string name);
        [DllImport("/usr/lib/libSystem.B.dylib")] static extern IntPtr dlsym(IntPtr handle, string symbol);
        [DllImport("/usr/lib/libSystem.B.dylib", EntryPoint = "_Block_copy")] static extern IntPtr CopyBlock(IntPtr block);
        [DllImport("/usr/lib/libSystem.B.dylib", EntryPoint = "_Block_release")] static extern void ReleaseBlock(IntPtr block);
        [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
        static extern IntPtr AddMonitor(IntPtr receiver, IntPtr selector, ulong mask, IntPtr handler);
        [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
        static extern void RemoveMonitor(IntPtr receiver, IntPtr selector, IntPtr monitor);
        [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
        static extern double ReadDelta(IntPtr receiver, IntPtr selector);
        [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
        static extern IntPtr ReadObject(IntPtr receiver, IntPtr selector);
        [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
        [return: MarshalAs(UnmanagedType.I1)]
        static extern bool ReadBool(IntPtr receiver, IntPtr selector);
        [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
        static extern void WriteBool(IntPtr receiver, IntPtr selector, [MarshalAs(UnmanagedType.I1)] bool value);
        [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
        static extern void SendVoid(IntPtr receiver, IntPtr selector);

        static readonly MouseEventHandler _handler = HandleEvent;
        static readonly BlockCopyHandler _copyHandler = Copy;
        static readonly BlockDisposeHandler _disposeHandler = Dispose;
        static IntPtr _eventClass, _removeMonitor, _deltaX, _deltaY;
        static IntPtr _monitor, _block, _descriptor, _signature;
        static IntPtr _window, _setMouseMoved;
        static bool _previousMouseMoved;
        static Vector2 _pending;
        static Exception _callbackError;
        static int _captureFrame;

        internal static void Reset()
        {
            _pending = Vector2.zero;
            _callbackError = null;
            _captureFrame = Time.frameCount;
            if (_monitor != IntPtr.Zero) return;
            try
            {
                _eventClass = objc_getClass("NSEvent");
                _removeMonitor = sel_registerName("removeMonitor:");
                _deltaX = sel_registerName("deltaX");
                _deltaY = sel_registerName("deltaY");
                var blockClass = dlsym(new IntPtr(-2), "_NSConcreteStackBlock");
                if (_eventClass == IntPtr.Zero || blockClass == IntPtr.Zero)
                    throw new InvalidOperationException("Could not initialize the macOS mouse event monitor.");
                var application = ReadObject(objc_getClass("NSApplication"), sel_registerName("sharedApplication"));
                var window = ReadObject(application, sel_registerName("keyWindow"));
                if (window == IntPtr.Zero) throw new InvalidOperationException("Could not find the active macOS game window.");
                _window = ReadObject(window, sel_registerName("retain"));
                _setMouseMoved = sel_registerName("setAcceptsMouseMovedEvents:");
                _previousMouseMoved = ReadBool(_window, sel_registerName("acceptsMouseMovedEvents"));
                WriteBool(_window, _setMouseMoved, true);
                _signature = Marshal.StringToHGlobalAnsi("@16@?0@8");
                _descriptor = Marshal.AllocHGlobal(Marshal.SizeOf<BlockDescriptor>());
                Marshal.StructureToPtr(new BlockDescriptor
                {
                    Size = new UIntPtr((uint)Marshal.SizeOf<Block>()), Signature = _signature,
                    Copy = Marshal.GetFunctionPointerForDelegate(_copyHandler), Dispose = Marshal.GetFunctionPointerForDelegate(_disposeHandler)
                }, _descriptor, false);
                var stackBlock = Marshal.AllocHGlobal(Marshal.SizeOf<Block>());
                try
                {
                    Marshal.StructureToPtr(new Block
                    {
                        Isa = blockClass, Flags = (1 << 25) | (1 << 30),
                        Invoke = Marshal.GetFunctionPointerForDelegate(_handler), Descriptor = _descriptor
                    }, stackBlock, false);
                    _block = CopyBlock(stackBlock);
                    if (_block == IntPtr.Zero) throw new InvalidOperationException("Could not allocate the macOS mouse callback.");
                    _descriptor = _signature = IntPtr.Zero;
                }
                finally { Marshal.FreeHGlobal(stackBlock); }
                ulong mask = (1UL << 5) | (1UL << 6) | (1UL << 7) | (1UL << 27);
                _monitor = AddMonitor(_eventClass, sel_registerName("addLocalMonitorForEventsMatchingMask:handler:"), mask, _block);
                if (_monitor == IntPtr.Zero) throw new InvalidOperationException("Could not register the macOS mouse event monitor.");
                Application.quitting += Release;
            }
            catch
            {
                Release();
                throw;
            }
        }

        static void Copy(IntPtr destination, IntPtr source) { }

        static void Dispose(IntPtr block)
        {
            var descriptor = Marshal.PtrToStructure<Block>(block).Descriptor;
            Marshal.FreeHGlobal(Marshal.PtrToStructure<BlockDescriptor>(descriptor).Signature);
            Marshal.FreeHGlobal(descriptor);
        }

        static IntPtr HandleEvent(IntPtr block, IntPtr mouseEvent)
        {
            try
            {
                if (_monitor != IntPtr.Zero && _callbackError == null && Application.isFocused && Time.frameCount != _captureFrame)
                {
                    _pending.x += (float)ReadDelta(mouseEvent, _deltaX);
                    _pending.y += (float)ReadDelta(mouseEvent, _deltaY);
                }
            }
            catch (Exception error)
            {
                _pending = Vector2.zero;
                _callbackError = error;
            }
            return mouseEvent;
        }

        internal static Vector2 Read()
        {
            if (_callbackError != null) throw new InvalidOperationException("Could not read macOS mouse events.", _callbackError);
            var delta = _pending;
            _pending = Vector2.zero;
            return delta;
        }

        internal static void Release()
        {
            Application.quitting -= Release;
            if (_monitor != IntPtr.Zero)
            {
                RemoveMonitor(_eventClass, _removeMonitor, _monitor);
                _monitor = IntPtr.Zero;
            }
            if (_window != IntPtr.Zero)
            {
                WriteBool(_window, _setMouseMoved, _previousMouseMoved);
                SendVoid(_window, sel_registerName("release"));
                _window = IntPtr.Zero;
            }
            if (_block != IntPtr.Zero) ReleaseBlock(_block);
            Marshal.FreeHGlobal(_descriptor);
            Marshal.FreeHGlobal(_signature);
            _block = _descriptor = _signature = IntPtr.Zero;
            _pending = Vector2.zero;
            _callbackError = null;
        }
    }
}
