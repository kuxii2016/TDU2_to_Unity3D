using System;
using System.Runtime.InteropServices;
using UnityEngine;

namespace TDU2.Vehicles
{
    /// <summary>
    /// Force Feedback über DirectInput 8 (dinput8.dll, in jedem Windows) – herstellerunabhängig: Thrustmaster (T150/T300/TX/
    /// T500/TMX …), Fanatec, Logitech, Simucube usw., sofern der Treiber des Herstellers installiert ist.
    /// Ohne Zusatz-DLL: die COM-Schnittstellen IDirectInput8W / IDirectInputDevice8W / IDirectInputEffect werden direkt über
    /// ihre Funktionstabellen aufgerufen (funktioniert mit Mono und IL2CPP).
    ///
    /// Effekte (laufen dauerhaft, es werden nur die Werte geändert): Konstantkraft (Lenkmoment/Stöße), Feder (Rückstellkraft),
    /// Dämpfer, Sinus (Rütteln auf Erde/Gras). Das eingebaute Selbstzentrieren des Lenkrads wird abgeschaltet.
    /// Das Gerät wird exklusiv (Hintergrund) übernommen – das betrifft nur Force Feedback, Unity liest Achsen/Knöpfe weiter.
    /// </summary>
    public sealed class TDU2DirectInputFfb : IDisposable
    {
        public string DeviceName { get; private set; } = "";
        public bool Ok => _dev != IntPtr.Zero;

        IntPtr _di, _dev, _effConst, _effSpring, _effDamper, _effSine;
        IntPtr _axes, _dir, _typeParams, _eff, _format, _objFormat, _guidX;
        int _lastConst = int.MinValue, _lastSpringCoef = -1, _lastSpringSat = -1, _lastDamper = -1, _lastSine = -1;

        // ------------------------------------------------------------------ Konstanten
        const uint DIRECTINPUT_VERSION = 0x0800;
        const uint DI8DEVCLASS_GAMECTRL = 4, DIEDFL_ATTACHEDONLY = 1, DIEDFL_FORCEFEEDBACK = 0x100;
        const uint DISCL_EXCLUSIVE = 1, DISCL_BACKGROUND = 8;
        const uint DIEFF_OBJECTOFFSETS = 0x02, DIEFF_CARTESIAN = 0x10;
        const uint DIEP_TYPESPECIFICPARAMS = 0x00000100, DIEP_START = 0x20000000, DIEP_DIRECTION = 0x40, DIEP_AXES = 0x20;
        const uint INFINITE = 0xFFFFFFFF, DIEB_NOTRIGGER = 0xFFFFFFFF;
        const uint DISFFC_RESET = 0x1, DISFFC_STOPALL = 0x2, DISFFC_SETACTUATORSON = 0x10;
        const int DI_FFNOMINALMAX = 10000;
        static readonly Guid IID_IDirectInput8W = new Guid("BF798031-483A-4DA2-AA99-5D64ED369700");
        static readonly Guid GUID_XAxis = new Guid("A36D02E0-C9F3-11CF-BFC7-444553540000");
        static readonly Guid GUID_ConstantForce = new Guid("13541C20-8E33-11D0-9AD0-00A0C9A06E35");
        static readonly Guid GUID_Sine = new Guid("13541C23-8E33-11D0-9AD0-00A0C9A06E35");
        static readonly Guid GUID_Spring = new Guid("13541C27-8E33-11D0-9AD0-00A0C9A06E35");
        static readonly Guid GUID_Damper = new Guid("13541C28-8E33-11D0-9AD0-00A0C9A06E35");

        // ------------------------------------------------------------------ native
        [DllImport("dinput8.dll")] static extern int DirectInput8Create(IntPtr hinst, uint version, ref Guid riid, out IntPtr ppv, IntPtr outer);
        [DllImport("kernel32.dll")] static extern IntPtr GetModuleHandle(string name);
        [DllImport("user32.dll")] static extern IntPtr GetActiveWindow();

        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate uint ReleaseFn(IntPtr self);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int CreateDeviceFn(IntPtr self, ref Guid guid, out IntPtr dev, IntPtr outer);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int EnumDevicesFn(IntPtr self, uint devType, EnumCb cb, IntPtr pvRef, uint flags);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int EnumCb(IntPtr instance, IntPtr pvRef);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int NoArgFn(IntPtr self);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int PtrArgFn(IntPtr self, IntPtr p);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int SetPropertyFn(IntPtr self, IntPtr guidProp, IntPtr header);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int SetCoopFn(IntPtr self, IntPtr hwnd, uint flags);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int CreateEffectFn(IntPtr self, ref Guid guid, IntPtr eff, out IntPtr effect, IntPtr outer);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int UIntArgFn(IntPtr self, uint v);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int SetParamsFn(IntPtr self, IntPtr eff, uint flags);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int StartFn(IntPtr self, uint iterations, uint flags);

        static T Fn<T>(IntPtr com, int slot) where T : Delegate
        {
            IntPtr vtbl = Marshal.ReadIntPtr(com);
            return (T)Marshal.GetDelegateForFunctionPointer(Marshal.ReadIntPtr(vtbl, slot * IntPtr.Size), typeof(T));
        }

        static void Release(ref IntPtr com)
        {
            if (com == IntPtr.Zero) return;
            try { Fn<ReleaseFn>(com, 2)(com); } catch (Exception) { }
            com = IntPtr.Zero;
        }

        // ------------------------------------------------------------------ Öffnen
        Guid _found; string _foundName; bool _haveFound; string _prefer;

        /// <summary>Erstes Force-Feedback-Gerät öffnen (prefer: Teil des Gerätenamens, z.B. der Unity-Joystickname).</summary>
        public bool Open(string prefer)
        {
            Dispose();
            try
            {
                var iid = IID_IDirectInput8W;
                int hr = DirectInput8Create(GetModuleHandle(null), DIRECTINPUT_VERSION, ref iid, out _di, IntPtr.Zero);
                if (hr < 0 || _di == IntPtr.Zero) { Debug.LogWarning("[TDU2] DirectInput8Create: 0x" + hr.ToString("X8")); return false; }
                _haveFound = false; _prefer = (prefer ?? "").ToLowerInvariant();
                EnumCb cb = OnDevice;
                Fn<EnumDevicesFn>(_di, 4)(_di, DI8DEVCLASS_GAMECTRL, cb, IntPtr.Zero, DIEDFL_ATTACHEDONLY | DIEDFL_FORCEFEEDBACK);
                GC.KeepAlive(cb);
                if (!_haveFound) { Debug.Log("[TDU2] DirectInput: kein Force-Feedback-Gerät gefunden"); Dispose(); return false; }

                var g = _found;
                hr = Fn<CreateDeviceFn>(_di, 3)(_di, ref g, out _dev, IntPtr.Zero);
                if (hr < 0 || _dev == IntPtr.Zero) { Debug.LogWarning("[TDU2] DirectInput CreateDevice: 0x" + hr.ToString("X8")); Dispose(); return false; }
                DeviceName = _foundName;

                BuildDataFormat();
                hr = Fn<PtrArgFn>(_dev, 11)(_dev, _format);                                   // SetDataFormat
                if (hr < 0) { Debug.LogWarning("[TDU2] DirectInput SetDataFormat: 0x" + hr.ToString("X8")); Dispose(); return false; }
                IntPtr hwnd = GetActiveWindow();
                if (hwnd == IntPtr.Zero) hwnd = System.Diagnostics.Process.GetCurrentProcess().MainWindowHandle;
                hr = Fn<SetCoopFn>(_dev, 13)(_dev, hwnd, DISCL_EXCLUSIVE | DISCL_BACKGROUND);  // SetCooperativeLevel
                if (hr < 0) { Debug.LogWarning("[TDU2] DirectInput SetCooperativeLevel: 0x" + hr.ToString("X8")); Dispose(); return false; }
                DisableAutoCenter();
                Fn<NoArgFn>(_dev, 7)(_dev);                                                     // Acquire
                Fn<UIntArgFn>(_dev, 22)(_dev, DISFFC_RESET);                                    // SendForceFeedbackCommand
                Fn<UIntArgFn>(_dev, 22)(_dev, DISFFC_SETACTUATORSON);

                AllocEffectBuffers();
                _effConst = CreateEffect(GUID_ConstantForce, 4);
                _effSpring = CreateEffect(GUID_Spring, 24);
                _effDamper = CreateEffect(GUID_Damper, 24);
                _effSine = CreateEffect(GUID_Sine, 16);
                if (_effConst == IntPtr.Zero && _effSpring == IntPtr.Zero) { Debug.LogWarning("[TDU2] DirectInput: Gerät nimmt keine Effekte an"); Dispose(); return false; }
                Debug.Log($"[TDU2] Force Feedback (DirectInput): \"{DeviceName}\" – Konstant {(_effConst != IntPtr.Zero)}, Feder {(_effSpring != IntPtr.Zero)}, Dämpfer {(_effDamper != IntPtr.Zero)}, Sinus {(_effSine != IntPtr.Zero)}");
                return true;
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[TDU2] DirectInput Force Feedback: " + ex.Message);
                Dispose();
                return false;
            }
        }

        int OnDevice(IntPtr inst, IntPtr pvRef)
        {
            // DIDEVICEINSTANCEW: +4 guidInstance, +20 guidProduct, +36 dwDevType, +40 tszInstanceName[260], +560 tszProductName[260]
            var guidBytes = new byte[16];
            Marshal.Copy(inst + 4, guidBytes, 0, 16);
            string name = Marshal.PtrToStringUni(inst + 560) ?? "";
            bool preferred = _prefer.Length > 0 && name.ToLowerInvariant().Contains(_prefer.Length > 12 ? _prefer.Substring(0, 12) : _prefer);
            if (!_haveFound || preferred)
            {
                _found = new Guid(guidBytes); _foundName = name; _haveFound = true;
                if (preferred) return 0;   // DIENUM_STOP
            }
            return 1;                       // DIENUM_CONTINUE
        }

        /// <summary>Minimales Datenformat (nur X-Achse an Versatz 0) – nötig für Acquire und die Effekt-Achse.</summary>
        void BuildDataFormat()
        {
            _guidX = Marshal.AllocHGlobal(16);
            Marshal.Copy(GUID_XAxis.ToByteArray(), 0, _guidX, 16);
            int objSize = IntPtr.Size == 8 ? 24 : 16;          // DIOBJECTDATAFORMAT {const GUID*, DWORD ofs, DWORD type, DWORD flags}
            _objFormat = Marshal.AllocHGlobal(objSize);
            Marshal.WriteIntPtr(_objFormat, 0, _guidX);
            Marshal.WriteInt32(_objFormat, IntPtr.Size, 0);                                   // dwOfs
            Marshal.WriteInt32(_objFormat, IntPtr.Size + 4, unchecked((int)(0x00000003 | 0x00FFFF00)));   // DIDFT_AXIS | DIDFT_ANYINSTANCE
            Marshal.WriteInt32(_objFormat, IntPtr.Size + 8, 0);
            int fmtSize = IntPtr.Size == 8 ? 32 : 24;           // DIDATAFORMAT {dwSize, dwObjSize, dwFlags, dwDataSize, dwNumObjs, rgodf}
            _format = Marshal.AllocHGlobal(fmtSize);
            Marshal.WriteInt32(_format, 0, fmtSize);
            Marshal.WriteInt32(_format, 4, objSize);
            Marshal.WriteInt32(_format, 8, 1);                  // DIDF_ABSAXIS
            Marshal.WriteInt32(_format, 12, 4);                 // dwDataSize (eine LONG-Achse)
            Marshal.WriteInt32(_format, 16, 1);                 // dwNumObjs
            Marshal.WriteIntPtr(_format, IntPtr.Size == 8 ? 24 : 20, _objFormat);
        }

        void DisableAutoCenter()
        {
            // DIPROPDWORD { DIPROPHEADER { dwSize=20, dwHeaderSize=16, dwObj=0, dwHow=DIPH_DEVICE }, dwData=DIPROPAUTOCENTER_OFF }
            IntPtr p = Marshal.AllocHGlobal(20);
            try
            {
                Marshal.WriteInt32(p, 0, 20); Marshal.WriteInt32(p, 4, 16); Marshal.WriteInt32(p, 8, 0); Marshal.WriteInt32(p, 12, 0); Marshal.WriteInt32(p, 16, 0);
                Fn<SetPropertyFn>(_dev, 6)(_dev, new IntPtr(9), p);   // DIPROP_AUTOCENTER = MAKEDIPROP(9)
            }
            catch (Exception) { }
            finally { Marshal.FreeHGlobal(p); }
        }

        void AllocEffectBuffers()
        {
            _axes = Marshal.AllocHGlobal(4); Marshal.WriteInt32(_axes, 0);   // DIJOFS_X in unserem Format = 0
            _dir = Marshal.AllocHGlobal(4); Marshal.WriteInt32(_dir, 0);
            _typeParams = Marshal.AllocHGlobal(32);
            _eff = Marshal.AllocHGlobal(IntPtr.Size == 8 ? 80 : 56);
        }

        /// <summary>DIEFFECT für eine Achse füllen (Versätze für x64 und x86).</summary>
        void FillEffect(int typeSize)
        {
            bool x64 = IntPtr.Size == 8;
            int size = x64 ? 80 : 56;
            for (int i = 0; i < size; i += 4) Marshal.WriteInt32(_eff, i, 0);
            Marshal.WriteInt32(_eff, 0, size);
            Marshal.WriteInt32(_eff, 4, (int)(DIEFF_CARTESIAN | DIEFF_OBJECTOFFSETS));
            Marshal.WriteInt32(_eff, 8, unchecked((int)INFINITE));       // dwDuration
            Marshal.WriteInt32(_eff, 12, 0);                              // dwSamplePeriod
            Marshal.WriteInt32(_eff, 16, DI_FFNOMINALMAX);                // dwGain
            Marshal.WriteInt32(_eff, 20, unchecked((int)DIEB_NOTRIGGER));
            Marshal.WriteInt32(_eff, 24, 0);
            Marshal.WriteInt32(_eff, 28, 1);                              // cAxes
            if (x64)
            {
                Marshal.WriteIntPtr(_eff, 32, _axes); Marshal.WriteIntPtr(_eff, 40, _dir); Marshal.WriteIntPtr(_eff, 48, IntPtr.Zero);
                Marshal.WriteInt32(_eff, 56, typeSize); Marshal.WriteIntPtr(_eff, 64, _typeParams); Marshal.WriteInt32(_eff, 72, 0);
            }
            else
            {
                Marshal.WriteIntPtr(_eff, 32, _axes); Marshal.WriteIntPtr(_eff, 36, _dir); Marshal.WriteIntPtr(_eff, 40, IntPtr.Zero);
                Marshal.WriteInt32(_eff, 44, typeSize); Marshal.WriteIntPtr(_eff, 48, _typeParams); Marshal.WriteInt32(_eff, 52, 0);
            }
        }

        IntPtr CreateEffect(Guid type, int typeSize)
        {
            for (int i = 0; i < 32; i += 4) Marshal.WriteInt32(_typeParams, i, 0);
            if (type == GUID_Sine) Marshal.WriteInt32(_typeParams, 12, 60000);   // DIPERIODIC.dwPeriod (µs)
            FillEffect(typeSize);
            var g = type;
            int hr = Fn<CreateEffectFn>(_dev, 18)(_dev, ref g, _eff, out IntPtr e, IntPtr.Zero);
            if (hr < 0 || e == IntPtr.Zero) return IntPtr.Zero;
            Fn<StartFn>(e, 7)(e, 1, 0);
            return e;
        }

        bool SetParams(IntPtr effect, int typeSize)
        {
            if (effect == IntPtr.Zero || _dev == IntPtr.Zero) return false;
            FillEffect(typeSize);
            int hr = Fn<SetParamsFn>(effect, 6)(effect, _eff, DIEP_TYPESPECIFICPARAMS | DIEP_START);
            if (hr < 0)
            {
                // Gerät verloren (Fokus, USB): neu übernehmen und Effekt neu starten
                Fn<NoArgFn>(_dev, 7)(_dev);
                hr = Fn<SetParamsFn>(effect, 6)(effect, _eff, DIEP_TYPESPECIFICPARAMS | DIEP_START);
            }
            return hr >= 0;
        }

        // ------------------------------------------------------------------ Effekte (Werte in Prozent wie das Logitech-SDK)
        /// <summary>Konstantkraft −100..100 (Vorzeichen = Richtung).</summary>
        public void Constant(int percent)
        {
            percent = Mathf.Clamp(percent, -100, 100);
            if (percent == _lastConst) return;
            _lastConst = percent;
            Marshal.WriteInt32(_typeParams, 0, percent * 100);
            SetParams(_effConst, 4);
        }

        /// <summary>Feder zur Mitte: Stärke und Sättigung 0..100.</summary>
        public void Spring(int coefficient, int saturation)
        {
            coefficient = Mathf.Clamp(coefficient, 0, 100); saturation = Mathf.Clamp(saturation, 0, 100);
            if (coefficient == _lastSpringCoef && saturation == _lastSpringSat) return;
            _lastSpringCoef = coefficient; _lastSpringSat = saturation;
            WriteCondition(coefficient, saturation);
            SetParams(_effSpring, 24);
        }

        /// <summary>Dämpfer 0..100.</summary>
        public void Damper(int coefficient)
        {
            coefficient = Mathf.Clamp(coefficient, 0, 100);
            if (coefficient == _lastDamper) return;
            _lastDamper = coefficient;
            WriteCondition(coefficient, 100);
            SetParams(_effDamper, 24);
        }

        /// <summary>Rütteln (Sinus, ~16 Hz) 0..100.</summary>
        public void Rumble(int magnitude)
        {
            magnitude = Mathf.Clamp(magnitude, 0, 100);
            if (magnitude == _lastSine) return;
            _lastSine = magnitude;
            Marshal.WriteInt32(_typeParams, 0, magnitude * 100);   // dwMagnitude
            Marshal.WriteInt32(_typeParams, 4, 0);                 // lOffset
            Marshal.WriteInt32(_typeParams, 8, 0);                 // dwPhase
            Marshal.WriteInt32(_typeParams, 12, 60000);            // dwPeriod (µs)
            SetParams(_effSine, 16);
        }

        void WriteCondition(int coefficient, int saturation)
        {
            // DICONDITION { lOffset, lPositiveCoefficient, lNegativeCoefficient, dwPositiveSaturation, dwNegativeSaturation, lDeadBand }
            Marshal.WriteInt32(_typeParams, 0, 0);
            Marshal.WriteInt32(_typeParams, 4, coefficient * 100);
            Marshal.WriteInt32(_typeParams, 8, coefficient * 100);
            Marshal.WriteInt32(_typeParams, 12, saturation * 100);
            Marshal.WriteInt32(_typeParams, 16, saturation * 100);
            Marshal.WriteInt32(_typeParams, 20, 0);
        }

        /// <summary>Alle Kräfte auf 0 (Pause, Menü, kein Fokus).</summary>
        public void Silence()
        {
            if (!Ok) return;
            try { Constant(0); Spring(0, 0); Damper(0); Rumble(0); } catch (Exception) { }
        }

        public void Dispose()
        {
            try { if (_dev != IntPtr.Zero) Fn<UIntArgFn>(_dev, 22)(_dev, DISFFC_STOPALL); } catch (Exception) { }
            Release(ref _effConst); Release(ref _effSpring); Release(ref _effDamper); Release(ref _effSine);
            if (_dev != IntPtr.Zero) { try { Fn<NoArgFn>(_dev, 8)(_dev); } catch (Exception) { } }   // Unacquire
            Release(ref _dev); Release(ref _di);
            foreach (var p in new[] { _axes, _dir, _typeParams, _eff, _format, _objFormat, _guidX }) if (p != IntPtr.Zero) Marshal.FreeHGlobal(p);
            _axes = _dir = _typeParams = _eff = _format = _objFormat = _guidX = IntPtr.Zero;
            _lastConst = int.MinValue; _lastSpringCoef = _lastSpringSat = _lastDamper = _lastSine = -1;
        }
    }
}
