using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;

namespace AprilTag {

//
// Job struct that wraps AprilTag pose estimator
//
struct PoseEstimationJob : Unity.Jobs.IJobParallelFor
{
    // Input data struct that simply wraps pointers to tag detection data
    public struct Input
    {
        unsafe Interop.Detection* p;

        unsafe public Input(ref Interop.Detection r)
          => p = (Interop.Detection*)Interop.Util.AsPointer(ref r);

        unsafe public ref Interop.Detection Ref
          => ref Interop.Util.AsRef<Interop.Detection>(p);
    }

    // I/O
    [ReadOnly] NativeArray<Input> _input;
    [WriteOnly] NativeArray<TagPose> _output;

    // Camera parameters
    double _tagSize;
    double _focalLength;
    double2 _focalCenter;

    // Constructor
    public PoseEstimationJob
      (NativeArray<Input> input, NativeArray<TagPose> output,
       int width, int height, float fov, float tagSize)
    {
        _input = input;
        _output = output;
        _tagSize = tagSize;
        _focalLength = height / 2 / math.tan(fov / 2);
        _focalCenter = math.double2(width, height) / 2;
    }

        public void Execute(int i)
        {
            ref var det = ref _input[i].Ref;

            var info = new Interop.DetectionInfo(ref det, _tagSize,
               _focalLength, _focalLength, _focalCenter.x, _focalCenter.y);

            using var pose = new Interop.Pose(ref info);

            var pos = pose.t.AsFloat3() * math.float3(1, -1, 1);

            var rot = math.quaternion(pose.R.AsFloat3x3());
            rot = rot.value * math.float4(-1, 1, -1, 1);

            var center = det.Center;
            var c0 = det.Corner1;
            var c1 = det.Corner2;
            var c2 = det.Corner3;
            var c3 = det.Corner4;

            _output[i] = new TagPose(
                det.ID,
                pos,
                rot,
                new Vector2((float)center.x, (float)center.y),
                new Vector2((float)c0.x, (float)c0.y),
                new Vector2((float)c1.x, (float)c1.y),
                new Vector2((float)c2.x, (float)c2.y),
                new Vector2((float)c3.x, (float)c3.y)
            );
        }
    }

} // namespace AprilTag
