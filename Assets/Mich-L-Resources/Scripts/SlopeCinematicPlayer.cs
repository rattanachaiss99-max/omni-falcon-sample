using UnityEngine;
#if UNITY_CINEMACHINE
using Unity.Cinemachine;
#endif

/// <summary>
/// SlopeCinematicPlayer:
/// ขับเคลื่อนตัวละคร Mich-L และมุมกล้อง Frontal Shot ตามข้อมูลความชันและความเร็ว
/// อ้างอิงตามข้อกำหนด: OSM_CINEMATIC_SPEC.md (Version 2.0.0 - Slope-Driven Dynamics)
/// </summary>
[ExecuteAlways]
[SelectionBase]
[AddComponentMenu("Cinematic/Slope Cinematic Player")]
public class SlopeCinematicPlayer : MonoBehaviour
{
    [Header("--- INPUT PARAMETERS (ดอยช้าง Profile หรือ Slider ทดสอบ) ---")]
    [Tooltip("ความชันของเส้นทาง (-20% ลงเขาชัน ถึง +20% ขึ้นเขาชัน)")]
    [Range(-20f, 20f)]
    public float slopeGradePercent = 0f;

    [Tooltip("มุมเอียงซ้าย/ขวาตามโค้งถนน (-15 ถึง +15 องศา)")]
    [Range(-15f, 15f)]
    public float leanAngleDeg = 0f;

    [Tooltip("ความเร็วในการเคลื่อนที่ (km/h)")]
    [Range(0f, 120f)]
    public float speedKmh = 45f;

    [Tooltip("ความขรุขระของพื้นผิวถนน (0 = เรียบเนียน, 1 = ทางลูกรัง/ขรุขระมาก)")]
    [Range(0f, 1f)]
    public float surfaceRoughness = 0.25f;

    [Header("--- AUTO SIMULATION (โหมดจำลองเส้นทางอัตโนมัติ) ---")]
    [Tooltip("เปิดใช้งานเพื่อให้ระบบเล่นการเปลี่ยนแปลงความชันและความเร็วตามเวลา")]
    public bool autoSimulate = false;
    [Range(0.1f, 3f)] public float simulationSpeed = 0.5f;

    [Header("--- TARGET RIG REFERENCES ---")]
    [Tooltip("Root Transform ของตัวละคร Mich-L")]
    public Transform characterRoot;

    [Tooltip("Animator ของตัวละคร เพื่อปรับสปีดการวิ่งตาม speedKmh")]
    public Animator characterAnimator;

    [Tooltip("คอนโทรลเลอร์ควบคุม Shader หน้าตาหุ่นยนต์ Mich-L")]
    public GlobalShaderRenderSettings robotShaderSettings;

    [Header("--- CAMERA REFERENCES ---")]
    [Tooltip("กล้องปกติ")]
    public Camera mainCamera;
#if UNITY_CINEMACHINE
    [Tooltip("กล้อง Cinemachine (ถ้ามี จะปรับ Lens FOV ให้ด้วย)")]
    public CinemachineCamera cinemachineCamera;
#endif
    [Tooltip("ความกว้างเลนส์ปกติ (Base FOV)")]
    public float baseCameraFov = 40f;

    [Header("--- DEBUG / DIAGNOSTICS ---")]
    [Tooltip("พิมพ์ log ตรวจสอบการทำงาน")]
    public bool showDebugLog = false;

    // Internal State Variables
    private Vector3 initialCharacterPos;
    private Quaternion initialCharacterRot;
    private bool initializedBaseTransforms = false;
    private float simTime = 0f;

    private void Awake()
    {
        AutoResolveReferences();
    }

    private void OnEnable()
    {
        AutoResolveReferences();
        CaptureInitialTransform();
    }

    private void OnValidate()
    {
        if (!Application.isPlaying)
        {
            AutoResolveReferences();
            ApplyDynamics();
        }
    }

    private void AutoResolveReferences()
    {
        if (characterRoot == null)
        {
            characterRoot = transform;
        }

        if (characterAnimator == null)
        {
            characterAnimator = GetComponentInChildren<Animator>();
        }

        if (robotShaderSettings == null)
        {
            robotShaderSettings = GetComponentInChildren<GlobalShaderRenderSettings>();
            if (robotShaderSettings == null)
            {
                robotShaderSettings = Object.FindFirstObjectByType<GlobalShaderRenderSettings>();
            }
        }

        if (mainCamera == null)
        {
            mainCamera = Camera.main;
            if (mainCamera == null)
            {
                mainCamera = Object.FindFirstObjectByType<Camera>();
            }
        }

#if UNITY_CINEMACHINE
        if (cinemachineCamera == null)
        {
            cinemachineCamera = Object.FindFirstObjectByType<CinemachineCamera>();
        }
#endif
    }

    private void CaptureInitialTransform()
    {
        if (characterRoot != null && !initializedBaseTransforms)
        {
            initialCharacterPos = characterRoot.localPosition;
            initialCharacterRot = characterRoot.localRotation;
            initializedBaseTransforms = true;
        }
    }

    private void Update()
    {
        // คำนวณค่า Auto Simulation ใน Update
        if (autoSimulate && Application.isPlaying)
        {
            simTime += Time.deltaTime * simulationSpeed;
            slopeGradePercent = Mathf.Sin(simTime * 0.8f) * 14f; // ขึ้น-ลงเนินระหว่าง -14% ถึง +14%
            leanAngleDeg = Mathf.Cos(simTime * 0.5f) * 10f;       // เลี้ยวซ้าย-ขวา
            speedKmh = 35f + Mathf.Sin(simTime * 1.2f) * 15f;    // สปีดระหว่าง 20 - 50 km/h
        }
    }

    /// <summary>
    /// สำคัญมาก: ต้องใช้ LateUpdate() เพราะถ้าทำใน Update()
    /// ตัว Animator หรือ PlayableDirector (Timeline) จะประเมินคีย์เฟรมทับ Transform ของตัวละครในเฟรมเดียวกัน
    /// ทำให้ดูเหมือนสคริปต์ไม่ทำงาน!
    /// </summary>
    private void LateUpdate()
    {
        if (characterRoot == null) return;
        ApplyDynamics();
    }

    /// <summary>
    /// คำนวณฟิสิกส์การเคลื่อนไหวตามสเปก v2.0.0
    /// </summary>
    public void ApplyDynamics()
    {
        if (characterRoot == null) return;

        CaptureInitialTransform();

        // -------------------------------------------------------------
        // A. คำนวณแกน Pitch (ขึ้น/ลงเขา) และ Roll (เอียงโค้ง)
        // -------------------------------------------------------------
        float targetPitchDeg = Mathf.Atan(slopeGradePercent / 100f) * Mathf.Rad2Deg * 0.65f;
        float targetLeanDeg = leanAngleDeg;

        Quaternion dynamicRotation = Quaternion.Euler(-targetPitchDeg, 0f, -targetLeanDeg);
        
        // ถ้าอยู่ในโหมดเล่นหรือมี Animator ให้คูณต่อท้ายท่าแอนิเมชันปัจจุบัน
        if (characterAnimator != null && characterAnimator.runtimeAnimatorController != null)
        {
            characterRoot.localRotation = characterRoot.localRotation * dynamicRotation;
        }
        else
        {
            characterRoot.localRotation = initialCharacterRot * dynamicRotation;
        }

        // -------------------------------------------------------------
        // B. คำนวณช่วงล่างและความสั่นสะเทือน (Suspension Jitter บนแกน Y)
        // -------------------------------------------------------------
        float jitterFreq = 20f + (speedKmh * 0.3f);
        float jitterAmp = surfaceRoughness * 0.035f * Mathf.Clamp01(speedKmh / 30f);
        float vibrationY = Mathf.Sin(Time.time * jitterFreq) * jitterAmp;

        characterRoot.localPosition += new Vector3(0f, vibrationY, 0f);

        // -------------------------------------------------------------
        // C. ปรับสปีดแอนิเมชันของ Mich-L ตามความเร็ว (speedKmh)
        // -------------------------------------------------------------
        if (characterAnimator != null)
        {
            characterAnimator.speed = Mathf.Clamp(speedKmh / 40f, 0.4f, 2.2f);
        }

        // -------------------------------------------------------------
        // D. การตอบสนองทางอารมณ์ของหน้าจอหุ่นยนต์ (Robot Screen Emotion)
        // -------------------------------------------------------------
        if (robotShaderSettings != null)
        {
            if (Mathf.Abs(slopeGradePercent) > 10f)
            {
                robotShaderSettings._TVNoise = Mathf.Clamp01((Mathf.Abs(slopeGradePercent) - 10f) * 0.1f);
            }
            else
            {
                robotShaderSettings._TVNoise = 0f;
            }
            robotShaderSettings.OnUpdateRenderSettings();
        }

        // -------------------------------------------------------------
        // E. การกำกับมุมกล้องหน้าตรง (Frontal Camera Directing)
        // -------------------------------------------------------------
        float fovDelta = -slopeGradePercent * 0.35f;
        float targetFov = Mathf.Clamp(baseCameraFov + fovDelta, 28f, 55f);

        if (mainCamera != null)
        {
            mainCamera.fieldOfView = targetFov;
        }

#if UNITY_CINEMACHINE
        if (cinemachineCamera != null)
        {
            cinemachineCamera.Lens.FieldOfView = targetFov;
        }
#endif

        if (showDebugLog)
        {
            Debug.Log($"[SlopeCinematicPlayer] Active: Pitch={targetPitchDeg:F1}°, Lean={targetLeanDeg:F1}°, Speed={speedKmh:F0}km/h, FOV={targetFov:F1}");
        }
    }

    /// <summary>
    /// ฟังก์ชันรับข้อมูลภายนอกจาก JSON สเปก v2.0.0
    /// </summary>
    public void SetDynamics(float slopeGrade, float speed, float lean, float roughness)
    {
        this.slopeGradePercent = slopeGrade;
        this.speedKmh = speed;
        this.leanAngleDeg = lean;
        this.surfaceRoughness = roughness;
        ApplyDynamics();
    }
}
