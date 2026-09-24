using System;
using System.Reflection;
using System.Runtime.CompilerServices;
using MphRead;
using MphRead.Entities;
using MphRead.Formats.Collision;
using MphRead.Mods.Network;
using OpenTK.Mathematics;
namespace MphRead.NetTest;
internal static class DynamicGeometryTests
{
    private sealed class Geometry(int id, bool continuous) : INetRewindableGeometry
    {
        public int NetGeometryId => id;
        public bool Continuous => continuous;
        public NetGeometryState State;
        public NetGeometryState CaptureNetworkCollisionState() => State;
        public void ApplyNetworkCollisionState(in NetGeometryState state) => State = state;
    }
    public static int Run()
    {
        try
        {
            var door = new Geometry(1, false); var field = new Geometry(2, false); var platform = new Geometry(3, true);
            var history = new NetDynamicGeometryHistory(new INetRewindableGeometry[] { door, field, platform });
            var origin = Matrix4.Identity;
            door.State = new(origin, origin, origin, Vector3.Zero, false);
            field.State = door.State with { Enabled = true };
            platform.State = door.State with { Enabled = true };
            history.Record(20);
            door.State = door.State with { Enabled = true }; field.State = field.State with { Enabled = false };
            var moved = Matrix4.CreateTranslation(10, 0, 0);
            platform.State = new(moved, moved.Inverted(), moved.Inverted(), new Vector3(10, 0, 0), true);
            history.Record(21);
            var liveDoor = door.State; var liveField = field.State; var livePlatform = platform.State;
            using (var scope = history.Begin(20))
            {
                NetArchitectureTests.Check(scope.Applied && !door.State.Enabled, "door closing after shot: historical trace passes");
                NetArchitectureTests.Check(field.State.Enabled, "force field disabling after shot: historical trace blocks");
                NetArchitectureTests.Check(platform.State.Center == Vector3.Zero, "platform and projectile use frame 20");
            }
            NetArchitectureTests.Check(door.State == liveDoor && field.State == liveField && platform.State == livePlatform, "exact present restoration");
            using (history.Begin(20.5))
            {
                NetArchitectureTests.Check(!door.State.Enabled && field.State.Enabled, "discrete flags never interpolate");
                NetArchitectureTests.Check(platform.State.Center.X == 5 && platform.State.Transform.Row3.X == 5, "continuous transform interpolation");
            }
            door.State = liveDoor with { Enabled = false }; field.State = liveField with { Enabled = true }; history.Record(22);
            using (history.Begin(21)) NetArchitectureTests.Check(door.State.Enabled && !field.State.Enabled, "door opening / field enabling retains source-frame obstruction");
            for (uint frame = 20; frame <= 22; frame++)
            {
                using var scope = history.Begin(frame);
                NetArchitectureTests.Check(scope.Applied && door.State.Enabled == (frame == 21), "catch-up walks obstacle history each frame");
            }
            var presentDoor = door.State;
            try { using var scope = history.Begin(20); throw new InvalidOperationException("injected projectile failure"); }
            catch (InvalidOperationException) { }
            NetArchitectureTests.Check(door.State == presentDoor && platform.State == livePlatform, "exception restores all geometry");
            using (var scope = history.Begin(1)) NetArchitectureTests.Check(!scope.Applied && door.State == presentDoor, "missing history uses present world");
            history.Record(148); // overwrite frame 20 at exactly 128 history cells
            using (var scope = history.Begin(20)) NetArchitectureTests.Check(!scope.Applied, "ring wrap cannot use another frame");
            history.Record(149);
            for (int i = 0; i < 100; i++) { using var scope = history.Begin(149); }
            long allocation = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 10000; i++) { using var scope = history.Begin(149); }
            NetArchitectureTests.Check(allocation == GC.GetAllocatedBytesForCurrentThread(), "warmed rewind has no allocations");
            // Exercise the production adapters with engine collision objects,
            // including exact cached-inverse restoration and discrete door bits.
            INetRewindableGeometry Adapter(string name, int id, object entity) => (INetRewindableGeometry)Activator.CreateInstance(
                typeof(NetDynamicGeometryHistory).GetNestedType(name, BindingFlags.NonPublic)!, new object[] { id, entity })!;
            var engineDoor = (DoorEntity)RuntimeHelpers.GetUninitializedObject(typeof(DoorEntity));
            var engineField = (ForceFieldEntity)RuntimeHelpers.GetUninitializedObject(typeof(ForceFieldEntity));
            var collision = new EntityCollision(new CollisionInstance("fixture", null!, true), engineDoor)
                { Transform = Matrix4.Identity, Inverse1 = Matrix4.Identity, Inverse2 = Matrix4.CreateTranslation(1, 2, 3) };
            var doorAdapter = Adapter("DoorGeometry", 1, engineDoor);
            var fieldAdapter = Adapter("FieldGeometry", 2, engineField);
            var meshAdapter = Adapter("MeshGeometry", 3, collision);
            var engineHistory = new NetDynamicGeometryHistory(new[] { doorAdapter, fieldAdapter, meshAdapter });
            engineDoor.Flags = DoorFlags.Open; engineHistory.Record(30);
            engineDoor.Flags = DoorFlags.Locked;
            fieldAdapter.ApplyNetworkCollisionState(fieldAdapter.CaptureNetworkCollisionState() with { Enabled = true });
            collision.Transform = Matrix4.CreateTranslation(20, 0, 0); collision.Inverse1 = collision.Transform.Inverted();
            var meshPresent = meshAdapter.CaptureNetworkCollisionState();
            using (engineHistory.Begin(30))
            {
                NetArchitectureTests.Check(engineDoor.Flags.TestFlag(DoorFlags.Open) && !engineField.Active
                    && collision.Transform == Matrix4.Identity, "production adapters apply historical door/field/platform collision");
                engineDoor.Flags |= DoorFlags.ShotOpen;
            }
            NetArchitectureTests.Check(engineDoor.Flags.TestFlag(DoorFlags.Locked) && engineDoor.Flags.TestFlag(DoorFlags.ShotOpen)
                && !engineDoor.Flags.TestFlag(DoorFlags.Open) && engineField.Active && meshAdapter.CaptureNetworkCollisionState() == meshPresent,
                "restore collision exactly while preserving new shot effects");
            Console.WriteLine("PASS: doors, force fields, moving collision, fractional sampling, catch-up, exception restoration, misses and 0 rewind allocations"); return 0;
        }
        catch (Exception e) { Console.Error.WriteLine(e); return 1; }
    }
}
