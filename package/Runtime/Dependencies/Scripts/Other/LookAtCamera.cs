using UnityEngine;
using System;

public class LookAtCamera : MonoBehaviour
{
    public LockAxis lockAxis = LockAxis.None;

    [Flags]
    public enum LockAxis
    {
        None = 0,
        X = 1 << 0,
        Y = 1 << 1,
        Z = 1 << 2
    }

    Camera mainCam => Camera.main;

    void Update()
    {
        if(mainCam == null) return;

        transform.LookAt(mainCam.transform.position);

        float xRotation = lockAxis.HasFlag(LockAxis.X) ? 0 : transform.rotation.eulerAngles.x;
        float yRotation = lockAxis.HasFlag(LockAxis.Y) ? 0 : transform.rotation.eulerAngles.y;
        float zRotation = lockAxis.HasFlag(LockAxis.Z) ? 0 : transform.rotation.eulerAngles.z;

        if (lockAxis.HasFlag(LockAxis.X) && lockAxis.HasFlag(LockAxis.Y) && lockAxis.HasFlag(LockAxis.Z)) Debug.LogWarning("[LookAtCamera] All Axis locked!");

        transform.rotation = Quaternion.Euler(xRotation, yRotation, zRotation);
    }
}
