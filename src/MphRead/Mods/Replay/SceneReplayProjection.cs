using System;
using System.IO;
using System.Security.Cryptography;
using MphRead.Entities;
using OpenTK.Mathematics;

namespace MphRead;

public partial class Scene
{
    /// <summary>Explicit simulation-owned presentation projection, separate from
    /// gameplay hashes. GPU names, draw interpolation, cameras and audio are omitted.</summary>
    internal string ReplayPresentationHash(uint recordingFrame)
    {
        using var stream = new MemoryStream(8192);
        using var writer = new BinaryWriter(stream);
        writer.Write((ushort)1);
        writer.Write(recordingFrame); writer.Write(Random.Rng2);
        void Vector(Vector3 value) { writer.Write(value.X); writer.Write(value.Y); writer.Write(value.Z); }
        void Matrix(Matrix4 value)
        {
            for (int row = 0; row < 4; row++) for (int column = 0; column < 4; column++) writer.Write(value[row, column]);
        }
        foreach (EntityBase entity in Entities)
        {
            writer.Write((int)entity.Type);
            writer.Write(entity is PlayerEntity player ? player.SlotIndex : entity.Id);
            writer.Write(entity.Initialized); writer.Write(entity.Hidden); writer.Write(entity.Alpha);
            writer.Write(entity.Recolor); Matrix(entity.Transform);
            writer.Write(entity.ReplayModels.Count);
            foreach (var model in entity.ReplayModels)
            {
                writer.Write(model.Model.Name); writer.Write(model.Active);
                var animation = model.AnimInfo;
                for (int slot = 0; slot < 2; slot++)
                {
                    writer.Write(animation.Index[slot]); writer.Write(animation.PrevIndex[slot]);
                    writer.Write(animation.Frame[slot]); writer.Write(animation.Step[slot]);
                    writer.Write((int)animation.Flags[slot]);
                }
                writer.Write(animation.Node.Slot); writer.Write(animation.Material.Slot);
                writer.Write(animation.Texture.Slot); writer.Write(animation.Texcoord.Slot);
            }
            if (entity is BeamProjectileEntity beam)
            {
                writer.Write(beam.DrawFuncId); Vector(beam.Color); Vector(beam.BackPosition);
                foreach (var position in beam.PastPositions) Vector(position);
            }
        }
        writer.Write(-1);
        writer.Write(_activeElements.Count);
        foreach (var element in _activeElements)
        {
            writer.Write(element.EffectId); writer.Write(element.ElementName);
            writer.Write(element.CreationTime); writer.Write(element.ExpirationTime);
            writer.Write(element.Expired); writer.Write((uint)element.Flags);
            Matrix(element.OwnTransform); Matrix(element.Transform);
            Vector(element.Acceleration); writer.Write(element.ParticleAmount);
            writer.Write(element.Particles.Count);
            foreach (var particle in element.Particles)
            {
                writer.Write(particle.ParticleId); writer.Write(particle.CreationTime);
                writer.Write(particle.ExpirationTime); Vector(particle.Position); Vector(particle.Speed);
                writer.Write(particle.Scale); writer.Write(particle.Rotation); writer.Write(particle.Alpha);
                writer.Write(particle.Red); writer.Write(particle.Green); writer.Write(particle.Blue);
                writer.Write(particle.RwField1); writer.Write(particle.RwField2);
                writer.Write(particle.RwField3); writer.Write(particle.RwField4);
            }
        }
        writer.Flush();
        return System.Convert.ToHexString(SHA256.HashData(stream.GetBuffer().AsSpan(0, (int)stream.Length)));
    }
}
