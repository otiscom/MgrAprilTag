using UnityEngine;

namespace AprilTag
{
    //
    // Tag pose structure for storing an estimated pose + raw 2D detection corners
    //
    public struct TagPose
    {
        public int ID { get; }
        public Vector3 Position { get; }
        public Quaternion Rotation { get; }

        // Prawdziwe wspó³rzêdne 2D z detekcji AprilTag w obrazie CPU
        public Vector2 Center { get; }
        public Vector2 Corner0 { get; }
        public Vector2 Corner1 { get; }
        public Vector2 Corner2 { get; }
        public Vector2 Corner3 { get; }

        public TagPose(int id, Vector3 position, Quaternion rotation)
        {
            ID = id;
            Position = position;
            Rotation = rotation;

            Center = Vector2.zero;
            Corner0 = Vector2.zero;
            Corner1 = Vector2.zero;
            Corner2 = Vector2.zero;
            Corner3 = Vector2.zero;
        }

        public TagPose(
            int id,
            Vector3 position,
            Quaternion rotation,
            Vector2 center,
            Vector2 corner0,
            Vector2 corner1,
            Vector2 corner2,
            Vector2 corner3)
        {
            ID = id;
            Position = position;
            Rotation = rotation;

            Center = center;
            Corner0 = corner0;
            Corner1 = corner1;
            Corner2 = corner2;
            Corner3 = corner3;
        }
    }
}