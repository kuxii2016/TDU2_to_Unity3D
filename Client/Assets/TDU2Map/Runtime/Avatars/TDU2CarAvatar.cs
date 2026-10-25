using System;
using System.Collections.Generic;
using TDU2.Map.Vehicles;
using UnityEngine;

namespace TDU2.Map.Avatars
{
    /// <summary>
    /// Fahrer bzw. Beifahrer im Auto wie im Spiel (anims/car, driveiklayer): Sitzpose "stance_turn", darÃ¼ber "turn" beim
    /// Lenkwinkel (0 s Vierteldrehung rechts, 0,52 s geradeaus, 1,04 s Vierteldrehung links), dann IK der HÃ¤nde an die
    /// Lenkradgriffe (HAND_HR_L/_R, Kinder von STEER_HR) und der FÃ¼ÃŸe an die Pedale (FOOT_HR_L/_R).
    /// Die Clip-Wurzel (ROOT) liegt auf dem HÃ¼ftpunkt DVRSEAT_HR (Beifahrer: PSGSEAT_HR, PSGFOOT_HR_*; Pose HIT_stand_car bzw.
    /// sho_stand_car). Der Avatar hÃ¤ngt unter CarModel.Visual (Spielraum, gespiegelt).
    /// </summary>
    [DefaultExecutionOrder(200)]   // nach TDU2Vehicle (Lenkrad) â€“ die HÃ¤nde folgen den Griffen im selben Frame
    public sealed class TDU2CarAvatar : MonoBehaviour
    {
        public CarModel Model;
        public bool Passenger;
        public Func<float> Steer;
        [Tooltip("Lenkrichtung der Körperanimation umkehren")] public bool InvertSteer;
        [Tooltip("Hände an die Lenkradgriffe (IK)")] public bool HandIK = true;
        [Tooltip("Füße an die Pedale (IK)")] public bool FootIK = true;
        [Tooltip("Kopf ausblenden, wenn die Kamera im Cockpit sitzt")] public bool HideHeadInCockpit = true;

        public Func<string> ConfigCode;
        public TDU2Avatar Avatar { get; private set; }

        public static bool ShowDrivers = true, ShowPassengers = true;
        public static string DebugClip;
        public static bool DebugIK = true;
        public static float DebugSpeed = 1f;
        public static readonly List<TDU2CarAvatar> All = new List<TDU2CarAvatar>();
        float _debugT;
        AnmClip _debug; string _debugName;
        string _code;
        float _nextCheck;
        Transform _seat, _handL, _handR, _footL, _footR;
        bool _bike;
        AnmClip _base, _turn;
        float _baseT;
        int _armL, _foreL, _ikHandL, _armR, _foreR, _ikHandR, _upLegL, _legL, _ikFootL, _upLegR, _legR, _ikFootR, _head;
        int _pelvis = -1;
        float _steerSmooth;

        public static TDU2CarAvatar Attach(GameObject car, CarModel model, AvatarConfig cfg, bool passenger, Func<float> steer)
        {
            if (car == null || model == null || model.Visual == null || !AvatarLibrary.Ready) return null;
            bool bike = model.Find("D_BIKE") != null;
            if (bike && passenger) return null;
            var seat = bike ? model.Find("DVRSEAT_HR")
                : model.Find(passenger ? "PSGSEAT_HR" : "DVRSEAT_HR") ?? model.Find(passenger ? "PSGSEAT_LR" : "DVRSEAT_LR");
            if (seat == null) return null;   // Autos ohne Sitz-Dummy
            var ca = car.AddComponent<TDU2CarAvatar>();
            ca.Model = model; ca.Passenger = passenger; ca.Steer = steer;
            ca._seat = seat; ca._bike = bike;
            ca.Rebuild(cfg);
            return ca;
        }

        /// <summary>Avatar mit neuem Aussehen neu aufbauen.</summary>
        public void Rebuild(AvatarConfig cfg)
        {
            if (Avatar != null) Destroy(Avatar.gameObject);
            _code = cfg?.Encode();
            Avatar = TDU2Avatar.Create(Model.Visual.transform, cfg, Passenger ? "TDU2 Beifahrer" : "TDU2 Fahrer");
            Avatar.InCar = true;
            Avatar.Built += OnBuilt;
        }

        void OnBuilt(TDU2Avatar a)
        {
            bool woman = a.Config != null && a.Config.Sex == 1;
            if (_bike)
            {
                // Motorrad: Normalfahrt lieber mit drive_front_speed, Lenkung über drive_back_turn
                _base = AvatarLibrary.Clip("drive_front_speed") ?? AvatarLibrary.Clip("drive_back");
                _turn = AvatarLibrary.Clip("drive_back_turn");
                if (_base == null) { _base = _turn; _baseT = _turn != null ? _turn.Length * 0.5f : 0f; }
                _handL = Model.Find("HAND_L");
                _handR = Model.Find("HAND_R");
                _footL = Model.Find("FOOT_L");
                _footR = Model.Find("FOOT_R");
            }
            else if (Passenger)
            {
                _base = AvatarLibrary.Clip(woman ? "sho_stand_car" : "hit_stand_car") ?? AvatarLibrary.Clip("stance_turn");
                _handL = _handR = null;
                _footL = Model.Find("PSGFOOT_HR_L") ?? Model.Find("PSGFOOT_LR_L");
                _footR = Model.Find("PSGFOOT_HR_R") ?? Model.Find("PSGFOOT_LR_R");
            }
            else
            {
                _base = AvatarLibrary.Clip("stance_turn");
                _turn = AvatarLibrary.Clip("turn");
                _handL = Model.Find("HAND_HR_L") ?? Model.Find("HAND_LR_L");
                _handR = Model.Find("HAND_HR_R") ?? Model.Find("HAND_LR_R");
                _footL = Model.Find("FOOT_HR_L") ?? Model.Find("FOOT_LR_L");
                _footR = Model.Find("FOOT_HR_R") ?? Model.Find("FOOT_LR_R");
            }
            _armL = a.Bone("ARM_L"); _foreL = a.Bone("FOREARM_L"); _ikHandL = a.Bone("IK_LEFT_HAND");
            _armR = a.Bone("ARM_R"); _foreR = a.Bone("FOREARM_R"); _ikHandR = a.Bone("IK_RIGHT_HAND");
            _upLegL = a.Bone("UPLEG_L"); _legL = a.Bone("LEG_L"); _ikFootL = a.Bone("IK_LEFT_FOOT");
            _upLegR = a.Bone("UPLEG_R"); _legR = a.Bone("LEG_R"); _ikFootR = a.Bone("IK_RIGHT_FOOT");
            _head = a.Bone("HEAD");
            _pelvis = a.Bone("PELVIS");
        }

        void LateUpdate()
        {
            if (ConfigCode != null && Time.unscaledTime >= _nextCheck)
            {
                _nextCheck = Time.unscaledTime + 2f;
                string c = ConfigCode();
                if (!string.IsNullOrEmpty(c) && c != _code) { var cfg = AvatarConfig.Decode(c); if (cfg != null) Rebuild(cfg); }
            }
            var a = Avatar;
            if (a == null || !a.Ready || _seat == null || Model == null || Model.Visual == null) return;
            a.SetVisible(Passenger ? ShowPassengers : ShowDrivers);
            var vis = Model.Visual.transform;
            var seatLocal = vis.worldToLocalMatrix.MultiplyPoint3x4(_seat.position);
            if (_bike)
            {
                // Motorrad: Bike-Clips sind im Bike-Modellraum mit absoluten Positionen.
                // Avatar an Modellursprung setzen, Pose anwenden, dann Beckenkorrektur.
                a.transform.localPosition = Vector3.zero;
                a.transform.localRotation = Quaternion.identity;
            }
            else
            {
                a.transform.localPosition = seatLocal;
                a.transform.localRotation = Quaternion.identity;
            }

            a.BeginPose();
            if (!string.IsNullOrEmpty(DebugClip))
            {
                if (_debugName != DebugClip) { _debugName = DebugClip; _debug = AvatarLibrary.Clip(DebugClip); _debugT = 0f; }
                _debugT += Time.deltaTime * DebugSpeed;
                if (_debug != null) a.ApplyClip(_debug, Mathf.Repeat(_debugT, Mathf.Max(0.05f, _debug.Length)), 1f, true);
            }
            else a.ApplyClip(_base, _baseT);
            if (_bike && _pelvis >= 0 && string.IsNullOrEmpty(DebugClip))
            {
                var hip = a.BonePosition(_pelvis);
                a.transform.localPosition = seatLocal - hip;
            }
            if (_turn != null && string.IsNullOrEmpty(DebugClip))
            {
                float s = Steer != null ? Mathf.Clamp(Steer(), -1f, 1f) : 0f;
                if (InvertSteer) s = -s;
                _steerSmooth = Mathf.MoveTowards(_steerSmooth, s, Time.deltaTime * 4f);
                float half = _turn.Length > 0f ? _turn.Length * 0.5f : 0.52f;
                float t = Mathf.Clamp(half - _steerSmooth * half, 0f, _turn.Length > 0f ? _turn.Length : 1.04f);
                a.ApplyClip(_turn, t, 1f, false);
            }
            if (HandIK && DebugIK)
            {
                if (_handL != null) a.SolveTwoBone(_armL, _foreL, _ikHandL, a.ToAvatarSpace(_handL.position));
                if (_handR != null) a.SolveTwoBone(_armR, _foreR, _ikHandR, a.ToAvatarSpace(_handR.position));
            }
            if (FootIK && DebugIK)
            {
                if (_footL != null) a.SolveTwoBone(_upLegL, _legL, _ikFootL, a.ToAvatarSpace(_footL.position));
                if (_footR != null) a.SolveTwoBone(_upLegR, _legR, _ikFootR, a.ToAvatarSpace(_footR.position));
            }
            a.CommitPose();

            if (HideHeadInCockpit && _head >= 0 && a.Bones != null)
            {
                var cam = Camera.main;
                bool inside = cam != null && (cam.transform.position - a.Bones[_head].position).sqrMagnitude < 0.45f * 0.45f;
                a.HideHead(inside);
            }
        }

        void OnEnable() { if (!All.Contains(this)) All.Add(this); }
        void OnDisable() { All.Remove(this); }

        void OnDestroy()
        {
            if (Avatar != null) Destroy(Avatar.gameObject);
        }
    }
}