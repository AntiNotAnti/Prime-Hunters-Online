using MphRead.Entities;
using MphRead.Formats;

namespace MphRead
{
    public sealed class SceneCameraSequences
    {
        public CameraSequence? Current { get; set; }
        public CameraSequence? Intro { get; set; }
        public CamSeqEntity? Entity { get; set; }
        internal CameraSequence?[] Sequences { get; } = new CameraSequence[199];
    }
}
