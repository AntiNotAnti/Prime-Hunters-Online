using System.Collections.Frozen;
using System.Linq;
using MphRead.Mods.MapGen;

namespace MphRead
{
    public static partial class Metadata
    {
        internal static bool IsBuiltInRoom(string name)
        {
            var room=GetRoomByName(name.ToUpperInvariant()).Item1;
            return room!=null&&room.Id<CustomRooms.FirstId;
        }
        // Called between scenes on the shell thread. Existing metadata objects
        // remain valid for callers holding an earlier snapshot.
        internal static void RegisterStudioPreview(MapDefinition definition)
        {
            CustomMapMusic.PreviewDefinition = definition;
            RegisterRoomSnapshot(definition);
        }
        internal static void RegisterDownloadedMap(MapDefinition definition)
        {
            RegisterRoomSnapshot(definition);
            CustomRooms.InstallSnapshot(definition);
        }
        private static void RegisterRoomSnapshot(MapDefinition definition)
        {
            if(IsBuiltInRoom(definition.Name))throw new MapAuthoringException("FP-MAP-010","A custom map cannot replace a built-in room.");
            var names=_roomIds.ToList();int id=names.FindIndex(n=>n==definition.Name);
            if(id<0){id=names.Count;names.Add(definition.Name);}
            var room=CustomRooms.MakeMetadata(definition,id);
            Read.InvalidateRoomModel(definition.Name);
            Formats.Collision.Collision.InvalidateRoom(room.CollisionPath);
            var rooms=RoomList.ToList();int index=rooms.FindIndex(r=>r.Name==definition.Name);
            if(index<0)rooms.Add(room);else rooms[index]=room;
            _roomIds=names.AsReadOnly();RoomList=rooms.AsReadOnly();RoomMetadata=rooms.ToFrozenDictionary(r=>r.Name);
        }
    }
}
