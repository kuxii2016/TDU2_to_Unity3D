using System;
using System.Collections.Generic;
using TDU2.Map.Vehicles;
using UnityEngine;

namespace TDU2.Map.Avatars
{
    /// <summary>
    /// Fahrer bzw. Beifahrer im Auto wie im Spiel (anims/car, driveiklayer): Sitzpose "stance_turn", darüber "turn" beim
    /// Lenkwinkel (0 s Vierteldrehung rechts, 0,52 s geradeaus, 1,04 s Vierteldrehung links), dann IK der Hände an die
    /// Lenkradgriffe (HAND_HR_L/_R, Kinder von STEER_HR) und der Füße an die Pedale (FOOT_HR_L/_R).
    /// Die Clip-Wurzel (ROOT) liegt auf dem Hüftpunkt DVRSEAT_HR (Beifahrer: PSGSEAT_HR, PSGFOOT_HR_*; Pose HIT_stand_car bzw.
    /// sho_stand_car). Der Avatar hängt unter CarModel.Visual (Spielraum, gespiegelt).
    /// </summary>
    [DefaultExecutionOrder(200)]   // nach TDU2Vehicle (Lenkrad) – die Hände folgen den Griffen im selben Frame
    public sealed class TDU2CarAvatar : MonoBehaviour
    {
        public CarModel Model;
        public bool Passenger;
        /// <summary>Lenkung −1 (links) … +1 (rechts); null = Mitte.</summary>
        public Func<float> Steer;
        [Tooltip("Lenkrichtung der Körperanimation umkehren")] public bool InvertSteer;
        [Tooltip("Hände an die Lenkradgriffe (IK)")] public bool HandIK = true;
        [Tooltip("Füße an die Pedale (IK)")] public bool FootIK = true;
        [Tooltip("Kopf ausblenden, wenn die Kamera im Cockpit sitzt")] public bool HideHeadInCockpit = true;

        /// <summary>Kodiertes Aussehen (AvatarConfig.Encode) – ändert es sich, wird der Avatar neu gebaut (Mitspieler, Kleidungsladen).</summary>
        public Func<string> ConfigCode;
        public TDU2Avatar Avatar { get; private set; }

        // Entwicklermenü (F1 -> Avatare)
        /// <summary>Fahrer/Beifahrer anzeigen.</summary>
        public static bool ShowDrivers = true, ShowPassengers = true;
        /// <summary>Statt der Sitzpose diesen Clip (avatarsdriver.bnk) in Schleife abspielen (null = normal).</summary>
        public static string DebugClip;
        /// <summary>IK der Hände/Füße an Lenkrad und Pedale.</summary>
        public static bool DebugIK = true;
        public static float DebugSpeed = 1f;
        public static readonly List<TDU2CarAvatar> All = new List<TDU2CarAvatar>();
        float _debugT;
        AnmClip _debug; string _debugName;
        string _code;
        float _nextCheck;
        Transform _seat, _handL, _handR, _footL, _footR;
        AnmClip _base, _turn;
        int _armL, _foreL, _ikHandL, _armR, _foreR, _ikHandR, _upLegL, _legL, _ikFootL, _upLegR, _legR, _ikFootR, _head;
        float _steerSmooth;

        /// <summary>Fahrer/Beifahrer an ein gebautes Auto hängen.</summary>
        public static TDU2CarAvatar Attach(GameObject car, CarModel model, AvatarConfig cfg, bool passenger, Func<float> steer)
        {
            if (car == null || model == null || model.Visual == null || !AvatarLibrary.Ready) return null;
            var seat = model.Find(passenger ? "PSGSEAT_HR" : "DVRSEAT_HR") ?? model.Find(passenger ? "PSGSEAT_LR" : "DVRSEAT_LR");
            if (seat == null) return null;   // Motorräder/Autos ohne Sitz-Dummy
            var ca = car.AddComponent<TDU2CarAvatar>();
            ca.Model = model; ca.Passenger = passenger; ca.Steer = steer;
            ca._seat = seat;
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
            if (Passenger)
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
            // Hüftpunkt des Sitzes im Spielraum des Autos (Avatar-Eltern = Visual)
            var vis = Model.Visual.transform;
            a.transform.localPosition = vis.worldToLocalMatrix.MultiplyPoint3x4(_seat.position);
            a.transform.localRotation = Quaternion.identity;

            a.BeginPose();
            if (!string.IsNullOrEmpty(DebugClip))
            {
                if (_debugName != DebugClip) { _debugName = DebugClip; _debug = AvatarLibrary.Clip(DebugClip); _debugT = 0f; }
                _debugT += Time.deltaTime * DebugSpeed;
                if (_debug != null) a.ApplyClip(_debug, Mathf.Repeat(_debugT, Mathf.Max(0.05f, _debug.Length)), 1f, true);
            }
            else a.ApplyClip(_base, 0f);
            if (_turn != null && string.IsNullOrEmpty(DebugClip))
            {
                float s = Steer != null ? Mathf.Clamp(Steer(), -1f, 1f) : 0f;
                if (InvertSteer) s = -s;
                _steerSmooth = Mathf.MoveTowards(_steerSmooth, s, Time.deltaTime * 4f);
                float t = Mathf.Clamp(0.52f - _steerSmooth * 0.52f, 0f, _turn.Length > 0f ? _turn.Length : 1.04f);
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
