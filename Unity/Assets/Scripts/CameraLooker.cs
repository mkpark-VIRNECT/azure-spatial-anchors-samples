using System.Collections;
using System.Collections.Generic;
using UnityEngine;

[ExecuteAlways]
public class CameraLooker : MonoBehaviour
{
    // Update is called once per frame
    void Update()
    {
        if(Camera.main != null)
        transform.LookAt(Camera.main.transform.position);
        transform.forward *= -1;
    }
}
