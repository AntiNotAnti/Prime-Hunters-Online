using System;
using System.IO;
using System.Linq;
using MphRead.Formats.Sound;
using SoundFlow.Components;
using SoundFlow.Providers;

namespace MphRead.Mods.MapGen
{
    public static class CustomMapMusic
    {
        private static SoundPlayer? _player;
        private static StreamDataProvider? _provider;
        private static MemoryStream? _stream;
        private static int _room = -1;
        private static float _gain;
        public static MapDefinition? PreviewDefinition { get; set; }

        public static bool TryPlay(int roomId)
        {
            var metadata = Metadata.GetRoomById(roomId, noThrow:true);
            var definition = PreviewDefinition?.Name == metadata?.Name ? PreviewDefinition
                : CustomRooms.Definitions.FirstOrDefault(d => d.Name == metadata?.Name);
            if (definition?.Audio is not {} settings) return false;
            if (_room == roomId && _player != null) return true;
            if (settings.GameMusic != null && Enum.TryParse<MusicId>(settings.GameMusic,true,out var music))
            { Music.PlayMusic(music); return true; }
            if (settings.Music == null || !MusicPlayer.Available) return false;
            try
            {
                byte[] bytes = MapAssets.Read(definition,settings.Music);
                Music.Stop();
                _stream = new MemoryStream(bytes, writable:false);
                _provider = new StreamDataProvider(MusicPlayer.Engine!,_stream);
                _player = new SoundPlayer(MusicPlayer.Engine!,MusicPlayer.Format,_provider) { IsLooping=settings.Loop };
                _gain=settings.Volume; _room=roomId; ApplyVolume();
                MusicPlayer.PlaybackDevice!.MasterMixer.AddComponent(_player);
                MusicPlayer.PlaybackDevice.Start(); _player.Play();
                return true;
            }
            catch(Exception ex)
            {
                Console.WriteLine("[map] Custom music could not load: "+ex.Message);
                Stop(); return false;
            }
        }
        public static void ApplyVolume(){if(_player!=null)_player.Volume=Music.UserVolume*_gain;}
        public static bool Pause(){if(_player==null)return false;_player.Pause();return true;}
        public static bool Resume(){if(_player==null)return false;_player.Play();return true;}
        public static void Stop()
        {
            if(_player!=null){_player.Stop();MusicPlayer.PlaybackDevice?.MasterMixer.RemoveComponent(_player);_player.Dispose();}
            _provider?.Dispose();_stream?.Dispose();_player=null;_provider=null;_stream=null;_room=-1;
        }
    }
}
