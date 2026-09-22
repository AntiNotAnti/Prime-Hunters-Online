using System.IO;
using MphRead.Formats.Collision;
using OpenTK.Mathematics;

namespace MphRead.Entities;

public partial class RoomEntity
{
    internal void WriteReplayActivation(BinaryWriter writer)
    {
        writer.Write(_portals.Count);
        foreach (Portal portal in _portals) { writer.Write(portal.Name); writer.Write(portal.Active); }
        writer.Write(_roomCollision.Count);
        foreach (CollisionInstance collision in _roomCollision)
        {
            writer.Write(collision.Active); writer.Write(collision.ConnectorName != null);
            if (collision.ConnectorName != null) writer.Write(collision.ConnectorName);
            writer.Write(collision.Translation.X); writer.Write(collision.Translation.Y); writer.Write(collision.Translation.Z);
        }
    }
    internal void RestoreReplayActivation(BinaryReader reader)
    {
        if (reader.ReadInt32() != _portals.Count) throw new InvalidDataException("Replay room portal count differs.");
        foreach (Portal portal in _portals)
        {
            if (reader.ReadString() != portal.Name) throw new InvalidDataException("Replay room portal identity differs.");
            portal.Active = reader.ReadBoolean();
        }
        if (reader.ReadInt32() != _roomCollision.Count) throw new InvalidDataException("Replay room collision count differs.");
        foreach (CollisionInstance collision in _roomCollision)
        {
            collision.Active = reader.ReadBoolean(); collision.ConnectorName = reader.ReadBoolean() ? reader.ReadString() : null;
            var translation = new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
            if (!float.IsFinite(translation.X) || !float.IsFinite(translation.Y) || !float.IsFinite(translation.Z))
                throw new InvalidDataException("Invalid replay collision transform.");
            collision.Translation = translation;
        }
    }
}
