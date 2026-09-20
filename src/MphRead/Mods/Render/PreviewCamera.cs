using OpenTK.Mathematics;

namespace MphRead
{
    // A custom map has no intro camera sequence -- those are authored, one per
    // shipped multiplayer room, and indexed by room ID. Without one the
    // preview is whatever the default camera happens to see, which for a map
    // built around a void is usually the void. This lets a map say where its
    // picture should be taken from.
    public partial class Scene
    {
        public void SetPreviewCamera(Vector3 position, Vector3 target)
        {
            _cameraMode = CameraMode.Roam;
            _inputMode = InputMode.CameraOnly;
            _cameraPosition = position;
            Vector3 facing = target - position;
            _cameraFacing = facing.LengthSquared < 0.0001f ? -Vector3.UnitZ : facing.Normalized();
            _cameraRight = Vector3.Cross(_cameraFacing, Vector3.UnitY);
            _cameraRight = _cameraRight.LengthSquared < 0.0001f
                ? Vector3.UnitX
                : _cameraRight.Normalized();
            _cameraUp = Vector3.Cross(_cameraRight, _cameraFacing).Normalized();
        }
    }
}
