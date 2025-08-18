using jKnepel.ProteusNet.Networking;
using jKnepel.ProteusNet.Networking.Packets;
using System;
using System.Collections.Generic;
using UnityEngine;

namespace jKnepel.ProteusNet.Components
{
    [DisallowMultipleComponent]
    [RequireComponent(typeof(NetworkObject))]
    [AddComponentMenu("ProteusNet/Network Transform")]
    public class NetworkTransform : NetworkBehaviour
    {
        public enum ETransformType
        {
            Transform,
            Rigidbody
        }

        [Flags]
        public enum ETransformValues
        {
            Nothing = 0,
            PositionX = 1,
            PositionY = 2,
            PositionZ = 4,
            PositionAll = PositionX | PositionY | PositionZ,
            RotationX = 8,
            RotationY = 16,
            RotationZ = 32,
            RotationAll = RotationX | RotationY | RotationZ,
            ScaleX = 64,
            ScaleY = 128,
            ScaleZ = 256,
            ScaleAll = ScaleX | ScaleY | ScaleZ,
            All = PositionAll | RotationAll | ScaleAll
        }
        
        private class TransformSnapshot
        {
            public uint Tick;
            public float Timestamp;
            public Vector3 Position;
            public Quaternion Rotation;
            public Vector3 EulerRotation;
            public Vector3 Scale;
            public Vector3 LinearVelocity;
            public Vector3 AngularVelocity;
        }

        private class TargetTransform
        {
            public Vector3 Position;
            public Quaternion Rotation;
            public Vector3 Scale;
            public Vector3 LinearVelocity;
            public Vector3 AngularVelocity;
            
            public TargetTransform() {}
            public TargetTransform(TransformSnapshot snapshot)
            {
                Position = snapshot.Position;
                Rotation = snapshot.Rotation;
                Scale = snapshot.Scale;
                LinearVelocity = snapshot.LinearVelocity;
                AngularVelocity = snapshot.AngularVelocity;
            }
        }
        
        #region fields and properties
        
        [SerializeField] private ENetworkChannel networkChannel = ENetworkChannel.UnreliableOrdered;
        
        [SerializeField] private ETransformType type;
        public ETransformType Type
        {
            get => type;
            set
            {
                if (type == value || NetworkObject.IsSpawned) return;
                type = value;

                switch (type)
                {
                    case ETransformType.Transform:
                        _rb = null;
                        break;
                    case ETransformType.Rigidbody:
                        if (!gameObject.TryGetComponent(out _rb))
                            _rb = gameObject.AddComponent<Rigidbody>();
                        break;
                    default:
                        throw new ArgumentOutOfRangeException();
                }
            }
        }

        [SerializeField] private ETransformValues synchronizeValues = ETransformValues.All;

        [SerializeField] private bool  positionUseWorld = true;
        [SerializeField] private float positionTolerance = 0.01f;
        [SerializeField] private float positionSmoothingMul = 5;
        [SerializeField] private bool  positionSnap = true;
        [SerializeField] private float positionSnapThreshold = 1;
        
        [SerializeField] private bool  rotationUseWorld = true;
        [SerializeField] private float rotationTolerance = 0.01f;
        [SerializeField] private float rotationSmoothingMul = 90;
        [SerializeField] private bool  rotationSnap = true;
        [SerializeField] private float rotationSnapThreshold = 90;
        
        [SerializeField] private bool  scaleUseWorld = true;
        [SerializeField] private float scaleTolerance = 0.01f;
        [SerializeField] private float scaleSmoothingMul = 5;
        [SerializeField] private bool  scaleSnap = true;
        [SerializeField] private float scaleSnapThreshold = 1;
        
        [SerializeField] private bool  useInterpolation = true;
        [SerializeField] private float interpolationInterval = .05f;
        [SerializeField] private bool  useExtrapolation = true;
        [SerializeField] private float extrapolationInterval = .2f;
        
        private Rigidbody _rb;
        private float KineticEnergy => Mathf.Pow(_rb.velocity.magnitude, 2) * 0.5f +
                                       Mathf.Pow(_rb.angularVelocity.magnitude, 2) * 0.5f;

        private (float, float, float) _lastPosition;
        private (float, float, float) _lastRotation;
        private (float, float, float) _lastScale;
        
        private float _authorityTimeOffset;
        private const float AUTHORITY_TIME_SMOOTHING = 0.5f;

        private Vector3 Position
        {
            get => positionUseWorld ? transform.position : transform.localPosition;
            set
            {
                if (positionUseWorld)
                    transform.position = value;
                else
                    transform.localPosition = value;
            }
        }
        
        private Quaternion Rotation
        {
            get => rotationUseWorld ? transform.rotation : transform.localRotation;
            set
            {
                if (rotationUseWorld)
                    transform.rotation = value;
                else
                    transform.localRotation = value;
            }
        }
        
        private Vector3 Scale
        {
            get => scaleUseWorld ? GetWorldScale(transform) : transform.localScale;
            set
            {
                if (scaleUseWorld)
                    SetWorldScale(transform, value);
                else
                    transform.localScale = value;
            }
        }

        private readonly List<TransformSnapshot> _receivedSnapshots = new();

        public bool UseInterpolation
        {
            get => useInterpolation;
            set => useInterpolation = value;
        }

        public float InterpolationInterval
        {
            get => interpolationInterval;
            set => interpolationInterval = value;
        }

        public bool UseExtrapolation
        {
            get => useExtrapolation;
            set => useExtrapolation = value;
        }

        public float ExtrapolationInterval
        {
            get => extrapolationInterval;
            set => extrapolationInterval = value;
        }

        // TODO : add component type configuration (CharacterController)
        // TODO : reset last values on authority change
        
        #endregion
        
        #region lifecycle

        private void Awake()
        {
            switch (Type)
            {
                case ETransformType.Transform:
                    _rb = null;
                    break;
                case ETransformType.Rigidbody:
                    if (!gameObject.TryGetComponent(out _rb))
                        Type = ETransformType.Transform;
                    break;
            }
        }
        
        private void Reset()
        {
            if (NetworkObject.IsSpawned)
                return;
            
            networkChannel = ENetworkChannel.UnreliableOrdered;
            
            type = transform.TryGetComponent(out _rb) 
                ? ETransformType.Rigidbody 
                : ETransformType.Transform;
            
            synchronizeValues = ETransformValues.All;

            positionUseWorld = true;
            positionTolerance = 0.01f;
            positionSmoothingMul = 5;
            positionSnap = true;
            positionSnapThreshold = 1;

            rotationUseWorld = true;
            rotationTolerance = 0.01f;
            rotationSmoothingMul = 90;
            rotationSnap = true;
            rotationSnapThreshold = 90;

            scaleUseWorld = true;
            scaleTolerance = 0.01f;
            scaleSmoothingMul = 5;
            scaleSnap = true;
            scaleSnapThreshold = 1;
            
            useInterpolation = true;
            interpolationInterval = .05f;
            useExtrapolation = true;
            extrapolationInterval = .2f;

            _lastPosition = (0,0,0);
            _lastRotation = (0,0,0);
            _lastScale = (0,0,0);
            
            _receivedSnapshots.Clear();
        }

        private void Update()
        {
            if (!IsSpawned || ShouldReplicate || _receivedSnapshots.Count == 0)
                return;

            UpdateTransform();
        }

        public override void OnRemoteSpawn(uint clientID)
        {
            if (synchronizeValues == ETransformValues.Nothing) 
                return;

            var packet = new TransformPacket.Builder(NetworkObject.ObjectIdentifier, NetworkManager.CurrentTick, true);
            
            var position = Position;
            var rotation = Rotation.eulerAngles;
            var scale = Scale;

            if (synchronizeValues.HasFlag(ETransformValues.PositionX))
                packet.WithPositionX(position.x);
            if (synchronizeValues.HasFlag(ETransformValues.PositionY))
                packet.WithPositionY(position.y);
            if (synchronizeValues.HasFlag(ETransformValues.PositionZ))
                packet.WithPositionZ(position.z);
            
            if (synchronizeValues.HasFlag(ETransformValues.RotationX))
                packet.WithRotationX(rotation.x);
            if (synchronizeValues.HasFlag(ETransformValues.RotationY))
                packet.WithRotationY(rotation.y);
            if (synchronizeValues.HasFlag(ETransformValues.RotationZ))
                packet.WithRotationZ(rotation.z);
            
            if (synchronizeValues.HasFlag(ETransformValues.ScaleX))
                packet.WithScaleX(scale.x);
            if (synchronizeValues.HasFlag(ETransformValues.ScaleY))
                packet.WithScaleY(scale.y);
            if (synchronizeValues.HasFlag(ETransformValues.ScaleZ))
                packet.WithScaleZ(scale.z);

            if (Type == ETransformType.Rigidbody)
                packet.WithRigidbody(_rb.velocity, _rb.angularVelocity);

            NetworkManager.Server.SendTransformInitial(clientID, this, packet.Build(), ENetworkChannel.ReliableOrdered);
        }
        
        public override void OnTickStarted(uint tick)
        {
            if (!ShouldReplicate || synchronizeValues == ETransformValues.Nothing) 
                return;

            var packet = new TransformPacket.Builder(NetworkObject.ObjectIdentifier, tick);
            
            var position = Position;
            var rotation = Rotation.eulerAngles;
            var scale = Scale;

            if (synchronizeValues.HasFlag(ETransformValues.PositionX) && Math.Abs(position.x - _lastPosition.Item1) >= positionTolerance)
                packet.WithPositionX(_lastPosition.Item1 = position.x);
            if (synchronizeValues.HasFlag(ETransformValues.PositionY) && Math.Abs(position.y - _lastPosition.Item2) >= positionTolerance)
                packet.WithPositionY(_lastPosition.Item2 = position.y);
            if (synchronizeValues.HasFlag(ETransformValues.PositionZ) && Math.Abs(position.z - _lastPosition.Item3) >= positionTolerance)
                packet.WithPositionZ(_lastPosition.Item3 = position.z);
            
            if (synchronizeValues.HasFlag(ETransformValues.RotationX) && Math.Abs(rotation.x - _lastRotation.Item1) >= rotationTolerance)
                packet.WithRotationX(_lastRotation.Item1 = rotation.x);
            if (synchronizeValues.HasFlag(ETransformValues.RotationY) && Math.Abs(rotation.y - _lastRotation.Item2) >= rotationTolerance)
                packet.WithRotationY(_lastRotation.Item2 = rotation.y);
            if (synchronizeValues.HasFlag(ETransformValues.RotationZ) && Math.Abs(rotation.z - _lastRotation.Item3) >= rotationTolerance)
                packet.WithRotationZ(_lastRotation.Item3 = rotation.z);
            
            if (synchronizeValues.HasFlag(ETransformValues.ScaleX) && Math.Abs(scale.x - _lastScale.Item1) >= scaleTolerance)
                packet.WithScaleX(_lastScale.Item1 = scale.x);
            if (synchronizeValues.HasFlag(ETransformValues.ScaleY) && Math.Abs(scale.y - _lastScale.Item2) >= scaleTolerance)
                packet.WithScaleY(_lastScale.Item2 = scale.y);
            if (synchronizeValues.HasFlag(ETransformValues.ScaleZ) && Math.Abs(scale.z - _lastScale.Item3) >= scaleTolerance)
                packet.WithScaleZ(_lastScale.Item3 = scale.z);

            if (Type == ETransformType.Rigidbody && KineticEnergy >= 0.01f)
                packet.WithRigidbody(_rb.velocity, _rb.angularVelocity);

            var build = packet.Build();
            if (build.Flags == TransformPacket.EFlags.Nothing)
                return;
            
            if (IsServer)
                NetworkManager.Server.SendTransformUpdate(this, build, networkChannel);
            else
                NetworkManager.Client.SendTransformUpdate(this, build, networkChannel);
        }

        #endregion
        
        #region private methods
        
        private Vector3 GetWorldScale(Transform target)
        {
            var worldScale = target.localScale;
            var parent = target.parent;

            while (parent != null)
            {
                worldScale = Vector3.Scale(worldScale, parent.localScale);
                parent = parent.parent;
            }

            return worldScale;
        }

        private void SetWorldScale(Transform target, Vector3 worldScale)
        {
            if (target.parent == null)
            {
                target.localScale = worldScale;
                return;
            }

            var parentWorldScale = GetWorldScale(target.parent);
            target.localScale = new(
                worldScale.x / parentWorldScale.x,
                worldScale.y / parentWorldScale.y,
                worldScale.z / parentWorldScale.z
            );
        }
        
        internal void ReceiveTransformUpdate(TransformPacket packet)
        {   
            // calculate offset to authority time for interpolation
            float authorityTimeSeconds = (float)packet.Tick / NetworkManager.Tickrate;
            float estimatedOffset = authorityTimeSeconds - Time.realtimeSinceStartup;
            float maxDelta = 2.0f / NetworkManager.Tickrate;

            // snaps offset on large desync
            _authorityTimeOffset = Mathf.Abs(_authorityTimeOffset - estimatedOffset) > maxDelta
                ? estimatedOffset
                : Mathf.Lerp(_authorityTimeOffset, estimatedOffset, AUTHORITY_TIME_SMOOTHING);

            var lastSnapshot = _receivedSnapshots.Count > 0 ? _receivedSnapshots[^1] : null;
            var position = Position;
            var rotation = Rotation.eulerAngles;
            var scale = Scale;

            var lastPosition = new Vector3(
                packet.PositionX ?? lastSnapshot?.Position.x ?? position.x,
                packet.PositionY ?? lastSnapshot?.Position.y ?? position.y,
                packet.PositionZ ?? lastSnapshot?.Position.z ?? position.z
            );
            var lastRotation = new Vector3(
                packet.RotationX ?? lastSnapshot?.EulerRotation.x ?? rotation.x,
                packet.RotationY ?? lastSnapshot?.EulerRotation.y ?? rotation.y,
                packet.RotationZ ?? lastSnapshot?.EulerRotation.z ?? rotation.z
            );
            var lastScale = new Vector3(
                packet.ScaleX ?? lastSnapshot?.Scale.x ?? scale.x,
                packet.ScaleY ?? lastSnapshot?.Scale.y ?? scale.y,
                packet.ScaleZ ?? lastSnapshot?.Scale.z ?? scale.z
            );
            
            _receivedSnapshots.Add(new()
            {
                Tick = packet.Tick,
                Timestamp = authorityTimeSeconds,
                Position = lastPosition,
                Rotation = Quaternion.Euler(lastRotation),
                EulerRotation = lastRotation,
                Scale = lastScale,
                LinearVelocity = packet.LinearVelocity ?? Vector3.zero,
                AngularVelocity = packet.AngularVelocity ?? Vector3.zero
            });
            
            // remove snapshots outside of interpolation interval
            float interpolationTime = Time.realtimeSinceStartup + _authorityTimeOffset - interpolationInterval;
            while (_receivedSnapshots.Count > 2 && _receivedSnapshots[1].Timestamp < interpolationTime)
                _receivedSnapshots.RemoveAt(0);
        }

        private void UpdateTransform()
        {
            var target = GetTargetTransform();
            if (target == null)
                return;
            
            if (positionSnap && Vector3.Distance(Position, target.Position) >= positionSnapThreshold)
                Position = target.Position;
            else
                Position = Vector3.MoveTowards(Position, target.Position, Time.deltaTime * positionSmoothingMul);

            if (rotationSnap && Quaternion.Angle(Rotation, target.Rotation) >= rotationSnapThreshold)
                Rotation = target.Rotation;
            else
                Rotation = Quaternion.RotateTowards(Rotation, target.Rotation, Time.deltaTime * rotationSmoothingMul);

            if (scaleSnap && Vector3.Distance(Scale, target.Scale) >= scaleSnapThreshold)
                Scale = target.Scale;
            else
                Scale = Vector3.MoveTowards(Scale, target.Scale, Time.deltaTime * scaleSmoothingMul);

            if (Type == ETransformType.Rigidbody)
            {
                _rb.velocity = target.LinearVelocity;
                _rb.angularVelocity = target.AngularVelocity;
            }
        }

        private TargetTransform GetTargetTransform()
        {
            if (!useInterpolation)
                return new(_receivedSnapshots[^1]);
            
            float renderingTime = Time.realtimeSinceStartup + _authorityTimeOffset - interpolationInterval;
            if (useExtrapolation)
            {   // shift rendering time forward by extrapolation interval
                renderingTime += extrapolationInterval;
                float now = Time.realtimeSinceStartup + _authorityTimeOffset;
                renderingTime = Mathf.Min(renderingTime, now);
            }
                
            var (left, right) = FindAdjacentSnapshots(renderingTime);

            // interpolate between snapshots
            if (left != null && right != null)
                return HermiteInterpolate(left, right, renderingTime);
             
            // extrapolate when newer snapshots are missing
            if (useExtrapolation && left != null && _receivedSnapshots.Count >= 2)
            {
                // only extrapolate when not overpredicting due to too many missing snapshots
                float lastSnapshotAge = Time.realtimeSinceStartup + _authorityTimeOffset - _receivedSnapshots[^1].Timestamp;
                if (!(lastSnapshotAge <= extrapolationInterval))
                    return new(_receivedSnapshots[^1]); 
                
                var prev = _receivedSnapshots[^2];
                var last = _receivedSnapshots[^1];
                return HermiteExtrapolate(prev, last, renderingTime);
            }

            // not enough snapshots or extrapolation is disabled
            return null;
        }

        private (TransformSnapshot left, TransformSnapshot right) FindAdjacentSnapshots(float timestamp)
        {
            if (_receivedSnapshots.Count == 0)
                return (null, null);
            if (_receivedSnapshots.Count == 1)
                return (_receivedSnapshots[0], null);

            for (int i = 0; i < _receivedSnapshots.Count - 1; i++)
            {
                var s1 = _receivedSnapshots[i];
                var s2 = _receivedSnapshots[i + 1];

                if (s1.Timestamp <= timestamp && timestamp <= s2.Timestamp)
                    return (s1, s2);
            }

            if (timestamp < _receivedSnapshots[0].Timestamp)
                return (null, _receivedSnapshots[0]); // too early
            if (timestamp > _receivedSnapshots[^1].Timestamp)
                return (_receivedSnapshots[^1], null); // too late

            return (null, null);
        }
        
        #region linear inter-/extrapolation
        
        private static TargetTransform LinearInterpolate(TransformSnapshot left, TransformSnapshot right, float timestamp)
        {
            var duration = right.Timestamp - left.Timestamp;
            var elapsed = timestamp - left.Timestamp;
            var t = Mathf.Clamp01(elapsed / duration);

            return new()
            {
                Position = Vector3.Lerp(left.Position, right.Position, t),
                Rotation = Quaternion.Lerp(left.Rotation, right.Rotation, t),
                Scale = Vector3.Lerp(left.Scale, right.Scale, t),
                LinearVelocity = Vector3.Lerp(left.LinearVelocity, right.LinearVelocity, t),
                AngularVelocity = Vector3.Lerp(left.AngularVelocity, right.AngularVelocity, t)
            };
        }
        
        private static TargetTransform LinearExtrapolate(TransformSnapshot left, TransformSnapshot right, float timestamp)
        {
            var extrapolateTime = timestamp - right.Timestamp;
            var deltaTime = right.Timestamp - left.Timestamp;
            deltaTime = Mathf.Max(deltaTime, 0.001f); // prevents NaN when snapshots were received in the same tick
            
            var targetPos = LinearExtrapolate(left.Position, right.Position, deltaTime, extrapolateTime);
            var targetScale = LinearExtrapolate(left.Scale, right.Scale, deltaTime, extrapolateTime);
            var targetRot = LinearExtrapolate(left.Rotation, right.Rotation, deltaTime, extrapolateTime);
            var targetLinVel = LinearExtrapolate(left.LinearVelocity, right.LinearVelocity, deltaTime, extrapolateTime);
            var targetAngVel = LinearExtrapolate(left.AngularVelocity, right.AngularVelocity, deltaTime, extrapolateTime);
            
            return new()
            {
                Position = targetPos,
                Rotation = targetRot,
                Scale = targetScale,
                LinearVelocity = targetLinVel,
                AngularVelocity = targetAngVel
            };
        }

        private static Vector3 LinearExtrapolate(Vector3 left, Vector3 right, float deltaTime, float extrapolateTime)
        {
            var deltaVector = (right - left) / deltaTime;
            var targetVector = right + deltaVector * extrapolateTime;
            return targetVector;
        }
        
        private static Quaternion LinearExtrapolate(Quaternion left, Quaternion right, float deltaTime, float extrapolateTime)
        {
            var t = 1 + extrapolateTime / deltaTime;
            return Quaternion.Slerp(left, right, t);
        }
        
        #endregion
        
        #region hermite inter-/extrapolation
        
        private static TargetTransform HermiteInterpolate(TransformSnapshot left, TransformSnapshot right, float timestamp)
        {
            float duration = right.Timestamp - left.Timestamp;
            float elapsed = timestamp - left.Timestamp;
            float t = Mathf.Clamp01(elapsed / duration);

            float t2 = t * t;
            float t3 = t2 * t;

            float h00 = 2f * t3 - 3f * t2 + 1f;
            float h10 = t3 - 2f * t2 + t;
            float h01 = -2f * t3 + 3f * t2;
            float h11 = t3 - t2;

            // Hermite for position
            Vector3 m0 = left.LinearVelocity * duration;
            Vector3 m1 = right.LinearVelocity * duration;
            Vector3 interpolatedPosition = h00 * left.Position + h10 * m0 + h01 * right.Position + h11 * m1;

            // Hermite for scale
            Vector3 sm0 = left.LinearVelocity * duration; // Ideally use ScaleVelocity
            Vector3 sm1 = right.LinearVelocity * duration;
            Vector3 interpolatedScale = h00 * left.Scale + h10 * sm0 + h01 * right.Scale + h11 * sm1;

            // Squad rotation interpolation
            Quaternion q0 = left.Rotation;
            Quaternion q1 = right.Rotation;
            Quaternion logQ0 = QuaternionLog(Quaternion.Inverse(q0) * q1);
            Quaternion logQ1 = QuaternionLog(Quaternion.Inverse(q1) * q0);

            Quaternion t0 = q0 * QuaternionExp(ScaleQuaternionLog(logQ0, -0.25f));
            Quaternion t1 = q1 * QuaternionExp(ScaleQuaternionLog(logQ1, -0.25f));

            Quaternion slerp1 = Quaternion.Slerp(q0, q1, t);
            Quaternion slerp2 = Quaternion.Slerp(t0, t1, t);
            Quaternion interpolatedRotation = Quaternion.Slerp(slerp1, slerp2, 2f * t * (1f - t)); // Squad formula

            return new()
            {
                Position = interpolatedPosition,
                Rotation = interpolatedRotation,
                Scale = interpolatedScale,
                LinearVelocity = Vector3.Lerp(left.LinearVelocity, right.LinearVelocity, t),
                AngularVelocity = Vector3.Lerp(left.AngularVelocity, right.AngularVelocity, t)
            };
        }
        
        private static TargetTransform HermiteExtrapolate(TransformSnapshot left, TransformSnapshot right, float timestamp)
        {
            var extrapolateTime = timestamp - right.Timestamp;
            var deltaTime = right.Timestamp - left.Timestamp;
            deltaTime = Mathf.Max(deltaTime, 0.001f); // avoid divide-by-zero

            // Tangents = velocity * deltaTime
            var posTangent0 = left.LinearVelocity * deltaTime;
            var posTangent1 = right.LinearVelocity * deltaTime;

            var scaleTangent0 = (right.Scale - left.Scale); // optional: scale delta/time if animating
            var scaleTangent1 = scaleTangent0;

            var position = HermiteExtrapolate(left.Position, right.Position, posTangent0, posTangent1, deltaTime, extrapolateTime);
            var scale = HermiteExtrapolate(left.Scale, right.Scale, scaleTangent0, scaleTangent1, deltaTime, extrapolateTime);
            var rotation = SquadExtrapolate(left.Rotation, right.Rotation, deltaTime, extrapolateTime);

            var linVel = HermiteExtrapolate(left.LinearVelocity, right.LinearVelocity, Vector3.zero, Vector3.zero, deltaTime, extrapolateTime);
            var angVel = HermiteExtrapolate(left.AngularVelocity, right.AngularVelocity, Vector3.zero, Vector3.zero, deltaTime, extrapolateTime);

            return new()
            {
                Position = position,
                Rotation = rotation,
                Scale = scale,
                LinearVelocity = linVel,
                AngularVelocity = angVel
            };
        }

        private static Vector3 HermiteExtrapolate(Vector3 p0, Vector3 p1, Vector3 t0, Vector3 t1, float deltaTime, float extrapolateTime)
        {
            float t = 1f + extrapolateTime / deltaTime;
            float t2 = t * t;
            float t3 = t2 * t;

            float h00 = 2 * t3 - 3 * t2 + 1;
            float h10 = t3 - 2 * t2 + t;
            float h01 = -2 * t3 + 3 * t2;
            float h11 = t3 - t2;

            return h00 * p0 + h10 * t0 + h01 * p1 + h11 * t1;
        }

        private static Quaternion SquadExtrapolate(Quaternion q0, Quaternion q1, float deltaTime, float extrapolateTime)
        {
            float t = 1f + extrapolateTime / deltaTime;

            Quaternion logQ = QuaternionLog(Quaternion.Inverse(q0) * q1);

            Quaternion expMinus = QuaternionExp(ScaleQuaternionLog(logQ, -0.25f));
            Quaternion expPlus  = QuaternionExp(ScaleQuaternionLog(logQ,  0.25f));

            Quaternion a = q0 * expMinus;
            Quaternion b = q1 * expPlus;

            Quaternion slerp1 = Quaternion.Slerp(q0, q1, t);
            Quaternion slerp2 = Quaternion.Slerp(a, b, t);
            return Quaternion.Slerp(slerp1, slerp2, 2 * t * (1 - t));
        }
        
        private static Quaternion ScaleQuaternionLog(Quaternion logQuat, float scale)
        {
            return new(logQuat.x * scale, logQuat.y * scale, logQuat.z * scale, 0f);
        }
        
        private static Quaternion QuaternionLog(Quaternion q)
        {
            if (q.w > 1.0f) q.Normalize(); // safeguard

            float a = Mathf.Acos(q.w);
            float sinA = Mathf.Sin(a);
            if (Mathf.Abs(sinA) < 0.0001f) return new(0, 0, 0, 0);

            float coeff = a / sinA;
            return new(q.x * coeff, q.y * coeff, q.z * coeff, 0);
        }

        private static Quaternion QuaternionExp(Quaternion q)
        {
            float a = Mathf.Sqrt(q.x * q.x + q.y * q.y + q.z * q.z);
            float sinA = Mathf.Sin(a);
            float cosA = Mathf.Cos(a);

            if (Mathf.Abs(a) < 0.0001f) return new(q.x, q.y, q.z, cosA);

            float coeff = sinA / a;
            return new(q.x * coeff, q.y * coeff, q.z * coeff, cosA);
        }
        
        #endregion
        
        #endregion
    }
}
