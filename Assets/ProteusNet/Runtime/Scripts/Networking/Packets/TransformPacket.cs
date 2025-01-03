using jKnepel.ProteusNet.Serializing;
using System;
using UnityEngine;

namespace jKnepel.ProteusNet.Networking.Packets
{
    public class TransformPacket
    {
        [Flags]
        public enum ETransformPacketFlag : ushort
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
            Rigidbody = 512
        }
        
        private TransformPacket(uint objectIdentifier)
        {
            ObjectIdentifier = objectIdentifier;
        }
        
        public static byte PacketType => (byte)EPacketType.Transform;
        public readonly uint ObjectIdentifier;
        public ETransformPacketFlag Flags { get; private set; } = 0;
        
        public float? PositionX { get; private set; }
        public float? PositionY { get; private set; }
        public float? PositionZ { get; private set; }
        
        public float? RotationX { get; private set; }
        public float? RotationY { get; private set; }
        public float? RotationZ { get; private set; }
        
        public float? ScaleX { get; private set; }
        public float? ScaleY { get; private set; }
        public float? ScaleZ { get; private set; }
        
        public Vector3? LinearVelocity { get; private set; }
        public Vector3? AngularVelocity { get; private set; }
        
        public static TransformPacket Read(Reader reader)
        {
            var packet = new TransformPacket(reader.ReadUInt32());
            packet.Flags = (ETransformPacketFlag)reader.ReadUInt16();
            
            if (packet.Flags.HasFlag(ETransformPacketFlag.PositionX))
            {
                packet.PositionX = reader.ReadSingle();
            }
            if (packet.Flags.HasFlag(ETransformPacketFlag.PositionY))
            {
                packet.PositionY = reader.ReadSingle();
            }
            if (packet.Flags.HasFlag(ETransformPacketFlag.PositionZ))
            {
                packet.PositionZ = reader.ReadSingle();
            }
            
            if (packet.Flags.HasFlag(ETransformPacketFlag.RotationX))
            {
                packet.RotationX = reader.ReadSingle();
            }
            if (packet.Flags.HasFlag(ETransformPacketFlag.RotationY))
            {
                packet.RotationY = reader.ReadSingle();
            }
            if (packet.Flags.HasFlag(ETransformPacketFlag.RotationZ))
            {
                packet.RotationZ = reader.ReadSingle();
            }
            
            if (packet.Flags.HasFlag(ETransformPacketFlag.ScaleX))
            {
                packet.ScaleX = reader.ReadSingle();
            }
            if (packet.Flags.HasFlag(ETransformPacketFlag.ScaleY))
            {
                packet.ScaleY = reader.ReadSingle();
            }
            if (packet.Flags.HasFlag(ETransformPacketFlag.ScaleZ))
            {
                packet.ScaleZ = reader.ReadSingle();
            }

            if (packet.Flags.HasFlag(ETransformPacketFlag.Rigidbody))
            {
                packet.LinearVelocity = reader.ReadVector3();
                packet.AngularVelocity = reader.ReadVector3();
            }

            return packet;
        }

        public static void Write(Writer writer, TransformPacket packet)
        {
            writer.WriteUInt32(packet.ObjectIdentifier);
            writer.WriteUInt16((ushort)packet.Flags);

            if (packet.Flags.HasFlag(ETransformPacketFlag.PositionX))
            {
                Debug.Assert(packet.PositionX != null, "PositionX is null and included in Flags");
                writer.WriteSingle((float)packet.PositionX);
            }
            if (packet.Flags.HasFlag(ETransformPacketFlag.PositionY))
            {
                Debug.Assert(packet.PositionY != null, "PositionY is null and included in Flags");
                writer.WriteSingle((float)packet.PositionY);
            }
            if (packet.Flags.HasFlag(ETransformPacketFlag.PositionZ))
            {
                Debug.Assert(packet.PositionZ != null, "PositionZ is null and included in Flags");
                writer.WriteSingle((float)packet.PositionZ);
            }
            
            if (packet.Flags.HasFlag(ETransformPacketFlag.RotationX))
            {
                Debug.Assert(packet.RotationX != null, "RotationX is null and included in Flags");
                writer.WriteSingle((float)packet.RotationX);
            }
            if (packet.Flags.HasFlag(ETransformPacketFlag.RotationY))
            {
                Debug.Assert(packet.RotationY != null, "RotationY is null and included in Flags");
                writer.WriteSingle((float)packet.RotationY);
            }
            if (packet.Flags.HasFlag(ETransformPacketFlag.RotationZ))
            {
                Debug.Assert(packet.RotationZ != null, "RotationZ is null and included in Flags");
                writer.WriteSingle((float)packet.RotationZ);
            }
            
            if (packet.Flags.HasFlag(ETransformPacketFlag.ScaleX))
            {
                Debug.Assert(packet.ScaleX != null, "ScaleX is null and included in Flags");
                writer.WriteSingle((float)packet.ScaleX);
            }
            if (packet.Flags.HasFlag(ETransformPacketFlag.ScaleY))
            {
                Debug.Assert(packet.ScaleY != null, "ScaleY is null and included in Flags");
                writer.WriteSingle((float)packet.ScaleY);
            }
            if (packet.Flags.HasFlag(ETransformPacketFlag.ScaleZ))
            {
                Debug.Assert(packet.ScaleZ != null, "ScaleZ is null and included in Flags");
                writer.WriteSingle((float)packet.ScaleZ);
            }

            if (packet.Flags.HasFlag(ETransformPacketFlag.Rigidbody))
            {
                Debug.Assert(packet.LinearVelocity != null && packet.AngularVelocity != null, "Rigidbody velocities are null and included in Flags");
                writer.WriteVector3((Vector3)packet.LinearVelocity);
                writer.WriteVector3((Vector3)packet.AngularVelocity);
            }
        }
        
        public class Builder
        {
            private readonly TransformPacket _packet;

            public Builder(uint objectIdentifier)
            {
                _packet = new(objectIdentifier);
            }

            public Builder WithPositionX(float x)
            {
                _packet.PositionX = x;
                _packet.Flags |= ETransformPacketFlag.PositionX;
                return this;
            }
            
            public Builder WithPositionY(float y)
            {
                _packet.PositionY = y;
                _packet.Flags |= ETransformPacketFlag.PositionY;
                return this;
            }
            
            public Builder WithPositionZ(float z)
            {
                _packet.PositionZ = z;
                _packet.Flags |= ETransformPacketFlag.PositionZ;
                return this;
            }

            public Builder WithRotationX(float x)
            {
                _packet.RotationX = x;
                _packet.Flags |= ETransformPacketFlag.RotationX;
                return this;
            }
            
            public Builder WithRotationY(float y)
            {
                _packet.RotationY = y;
                _packet.Flags |= ETransformPacketFlag.RotationY;
                return this;
            }
            
            public Builder WithRotationZ(float z)
            {
                _packet.RotationZ = z;
                _packet.Flags |= ETransformPacketFlag.RotationZ;
                return this;
            }
            
            public Builder WithScaleX(float x)
            {
                _packet.ScaleX = x;
                _packet.Flags |= ETransformPacketFlag.ScaleX;
                return this;
            }
            
            public Builder WithScaleY(float y)
            {
                _packet.ScaleY = y;
                _packet.Flags |= ETransformPacketFlag.ScaleY;
                return this;
            }
            
            public Builder WithScaleZ(float z)
            {
                _packet.ScaleZ = z;
                _packet.Flags |= ETransformPacketFlag.ScaleZ;
                return this;
            }
            
            public Builder WithRigidbody(Vector3 linearVelocity, Vector3 angularVelocity)
            {
                _packet.LinearVelocity = linearVelocity;
                _packet.AngularVelocity = angularVelocity;
                _packet.Flags |= ETransformPacketFlag.Rigidbody;
                return this;
            }
            
            public TransformPacket Build()
            {
                return _packet;
            }
        }
    }
}