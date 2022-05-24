using Cinemachine;
using FishNet;
using FishNet.Object;
using FishNet.Object.Prediction;
using StarterAssets;
using UnityEngine;
using UnityEngine.InputSystem;

public class PredictionMotor : NetworkBehaviour
{
    [Header("Player")]
    [Tooltip("Move speed of the character in m/s")]
    public float MoveSpeed = 2.0f;

    [Tooltip("How fast the character turns to face movement direction")]
    [Range(10f, 30f)]
    public float RotationSmoothTime = 12f;

    public AudioClip[] FootstepAudioClips;
    [Range(0, 1)] public float FootstepAudioVolume = 0.5f;

    [Header("Cinemachine")]
    [Tooltip("The follow target set in the Cinemachine Virtual Camera that the camera will follow")]
    public GameObject CinemachineCameraTarget;

    [Tooltip("How far in degrees can you move the camera up")]
    public float TopClamp = 70.0f;

    [Tooltip("How far in degrees can you move the camera down")]
    public float BottomClamp = -30.0f;

    [Tooltip("Additional degress to override the camera. Useful for fine tuning camera position when locked")]
    public float CameraAngleOverride = 0.0f;

    [Tooltip("For locking the camera position on all axis")]
    public bool LockCameraPosition = false;

    // cinemachine
    float _cinemachineTargetYaw, _cinemachineTargetPitch;

    // player
    float _targetRotation = 0.0f, _verticalVelocity;

    // animation IDs
    int _animIDIsWalking, _animIDMotionSpeed;

    PlayerInput _playerInput;

    Animator _animator;
    CharacterController _controller;
    StarterAssetsInputs _input;
    GameObject _mainCamera;

    const float _threshold = 0.01f;

    bool IsCurrentDeviceMouse
    {
        get
        {
#if ENABLE_INPUT_SYSTEM && STARTER_ASSETS_PACKAGES_CHECKED
            if (_playerInput == null) return false;

            return _playerInput.currentControlScheme == "KeyboardMouse";
#else
			return false;
#endif
        }
    }

    //MoveData for client simulation
    MoveData _clientMoveData;

    //MoveData for replication
    public struct MoveData
    {
        public Vector2 Move;
        public float CameraEulerY;
    }

    //ReconcileData for Reconciliation
    public struct ReconcileData
    {
        public Vector3 Position;
        public Quaternion Rotation;
        public float VerticalVelocity;

        public ReconcileData(Vector3 position, Quaternion rotation, float verticalVelocity)
        {
            Position = position;
            Rotation = rotation;
            VerticalVelocity = verticalVelocity;
        }
    }

    void Awake()
    {
        InstanceFinder.TimeManager.OnTick += TimeManager_OnTick;
        InstanceFinder.TimeManager.OnUpdate += TimeManager_OnUpdate;

        _controller = GetComponent<CharacterController>();

        _animator = GetComponent<Animator>();
    }

    void OnDestroy()
    {
        if (InstanceFinder.TimeManager != null)
        {
            InstanceFinder.TimeManager.OnTick -= TimeManager_OnTick;
            InstanceFinder.TimeManager.OnUpdate -= TimeManager_OnUpdate;
        }
    }

    void LateUpdate()
    {
        if (base.IsOwner) CameraRotation();
    }

    public override void OnStartClient()
    {
        base.OnStartClient();

        _controller.enabled = (base.IsServer || base.IsOwner);

        if (base.IsOwner)
        {
            GameObject.FindGameObjectWithTag("PlayerFollowCamera").GetComponent<CinemachineVirtualCamera>().Follow = CinemachineCameraTarget.transform;
            _mainCamera = GameObject.FindGameObjectWithTag("MainCamera");
            _playerInput = GetComponent<PlayerInput>();
            _playerInput.enabled = true;
            _input = GetComponent<StarterAssetsInputs>();
        }
    }

    public override void OnStartNetwork()
    {
        base.OnStartNetwork();

        _cinemachineTargetYaw = CinemachineCameraTarget.transform.rotation.eulerAngles.y;

        AssignAnimationIDs();
    }

    void TimeManager_OnTick()
    {
        if (base.IsOwner)
        {
            Reconciliation(default, false);
            CheckInput(out MoveData md);
            Move(md, false);
        }
        if (base.IsServer)
        {
            Move(default, true);
            ReconcileData rd = new ReconcileData(transform.position, transform.rotation, _verticalVelocity);
            Reconciliation(rd, true);
        }
    }

    void TimeManager_OnUpdate()
    {
        if (base.IsOwner)
            MoveWithData(_clientMoveData, Time.deltaTime);
    }

    [Reconcile]
    void Reconciliation(ReconcileData rd, bool asServer)
    {
        transform.position = rd.Position;
        transform.rotation = rd.Rotation;
        _verticalVelocity = rd.VerticalVelocity;
    }

    void CheckInput(out MoveData md)
    {
        md = new MoveData()
        {
            Move = _input.move,
            CameraEulerY = _mainCamera.transform.eulerAngles.y,
        };
    }

    [Replicate]
    void Move(MoveData md, bool asServer, bool replaying = false)
    {
        if (asServer || replaying)
        {
            MoveWithData(md, (float)base.TimeManager.TickDelta);
        }
        else if (!asServer)
            _clientMoveData = md;
    }

    void AssignAnimationIDs()
    {
        _animIDIsWalking = Animator.StringToHash("IsWalking");
        _animIDMotionSpeed = Animator.StringToHash("MotionSpeed");
    }

    void CameraRotation()
    {
        // if there is an input and camera position is not fixed
        if (_input.look.sqrMagnitude >= _threshold && !LockCameraPosition)
        {
            //Don't multiply mouse input by Time.deltaTime;
            float deltaTimeMultiplier = IsCurrentDeviceMouse ? 1.0f : Time.deltaTime;

            _cinemachineTargetYaw += _input.look.x * deltaTimeMultiplier;
            _cinemachineTargetPitch += _input.look.y * deltaTimeMultiplier;
        }

        // clamp our rotations so our values are limited 360 degrees
        _cinemachineTargetYaw = ClampAngle(_cinemachineTargetYaw, float.MinValue, float.MaxValue);
        _cinemachineTargetPitch = ClampAngle(_cinemachineTargetPitch, BottomClamp, TopClamp);

        // Cinemachine will follow this target
        CinemachineCameraTarget.transform.rotation = Quaternion.Euler(_cinemachineTargetPitch + CameraAngleOverride,
            _cinemachineTargetYaw, 0.0f);
    }

    void MoveWithData(MoveData md, float delta)
    {
        if (md.Move == Vector2.zero)
        {
            _animator.SetBool(_animIDIsWalking, false);
            _animator.SetFloat(_animIDMotionSpeed, 1f);
            return;
        }

        // normalise input direction
        Vector3 inputDirection = new Vector3(md.Move.x, 0.0f, md.Move.y).normalized;

        // note: Vector2's != operator uses approximation so is not floating point error prone, and is cheaper than magnitude
        // if there is a move input rotate player when the player is moving
        if (md.Move != Vector2.zero)
        {
            _targetRotation = Mathf.Atan2(inputDirection.x, inputDirection.z) * Mathf.Rad2Deg + md.CameraEulerY;

            // rotate to face input direction relative to camera position
            transform.rotation = Quaternion.Slerp(transform.rotation, Quaternion.Euler(0.0f, _targetRotation, 0.0f), RotationSmoothTime * delta);
        }

        Vector3 targetDirection = Quaternion.Euler(0.0f, _targetRotation, 0.0f) * Vector3.forward;

        // move the player
        if (md.Move == Vector2.zero)
            _controller.Move(new Vector3(0.0f, _verticalVelocity, 0.0f) * delta);
        else
            _controller.Move(targetDirection.normalized * (MoveSpeed * delta) + new Vector3(0.0f, _verticalVelocity, 0.0f) * delta);


        _animator.SetBool(_animIDIsWalking, true);
        _animator.SetFloat(_animIDMotionSpeed, 1f);
    }

    static float ClampAngle(float lfAngle, float lfMin, float lfMax)
    {
        if (lfAngle < -360f) lfAngle += 360f;
        if (lfAngle > 360f) lfAngle -= 360f;
        return Mathf.Clamp(lfAngle, lfMin, lfMax);
    }
}