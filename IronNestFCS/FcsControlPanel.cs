using Il2CppTMPro;
using IronNestFCS.Abstractions;
using MelonLoader;
using UnityEngine;
using Object = UnityEngine.Object;

namespace IronNestFCS;

/// <summary>
/// Stable Host-owned in-world FCS controls. F9 may replace TaskSystem Logic, but it must not recreate
/// these controls or reset the player's shell / AutoFire / MaxCharge intent.
/// </summary>
internal sealed class FcsControlPanel : IFcsControlUi
{
    private const float UiStartX = 0.425f;
    private const float UiStartY = -0.65f;
    private const float UiRowX = 0.05f;
    private const float UiRowY = 0.0045f;
    private const float AmmoLeftZ = -18.4181f;
    private const float RightColumnZ = -18.5881f;
    private const int LeftAmmoCount = 13;
    private const int RightAmmoStartRow = 6;

    private readonly Func<int, ShellTypeCode, bool> _trySubmitTarget;
    private readonly Action _onAutoFireEnabled;
    private readonly List<GameObject> _ownedObjects = new();
    private readonly List<GameObject> _shellButtons = new();
    private readonly Dictionary<int, GameObject> _targetButtons = new();
    private readonly StableClickRaycaster _clicks = new();

    private ShellTypeCode[] _availableShells = Array.Empty<ShellTypeCode>();
    private ShellTypeCode _selectedShell = ShellTypeCode.HE;
    private bool _availabilityKnown;
    private bool _isChinese;
    private bool _initialized;
    private TextMeshPro? _autoFireLabel;
    private TextMeshPro? _maxChargeLabel;

    public bool AutoFire { get; private set; }
    public bool MaxCharge { get; private set; }

    public FcsControlPanel(
        Func<int, ShellTypeCode, bool> trySubmitTarget,
        Action onAutoFireEnabled)
    {
        _trySubmitTarget = trySubmitTarget;
        _onAutoFireEnabled = onAutoFireEnabled;
    }

    public void Activate()
    {
        if (_initialized)
            return;

        _initialized = true;
        InitializeTargetButtons();
        RebuildShellButtons();
    }

    public void PublishAvailableShells(ShellTypeCode[] shells)
    {
        var next = (shells ?? Array.Empty<ShellTypeCode>())
            .Where(shell => shell != ShellTypeCode.None)
            .Distinct()
            .OrderBy(shell => (int)shell)
            .ToArray();

        var changed = !_availabilityKnown || !_availableShells.SequenceEqual(next);
        _availabilityKnown = true;
        if (!changed)
            return;

        _availableShells = next;
        if (_availableShells.Length > 0 && !_availableShells.Contains(_selectedShell))
        {
            _selectedShell = _availableShells.Contains(ShellTypeCode.HE)
                ? ShellTypeCode.HE
                : _availableShells[0];
        }

        if (_initialized)
            RebuildShellButtons();
    }

    public void PublishLanguage(bool isChinese)
    {
        if (_isChinese == isChinese)
            return;

        _isChinese = isChinese;
        if (_initialized)
            RefreshToggleLabels();
    }

    public void Update()
    {
        if (_initialized && Application.isFocused)
            _clicks.Update();
    }

    public void CompleteTargetSubmission(int targetId)
    {
        SetTargetAvailable(targetId);
    }

    public void ResetTransientInputs()
    {
        foreach (var targetId in _targetButtons.Keys.ToArray())
            SetTargetAvailable(targetId);
    }

    private void SetTargetAvailable(int targetId)
    {
        if (!_targetButtons.TryGetValue(targetId, out var button) || button == null)
            return;

        SetColor(button, Color.red);
        var collider = button.GetComponent<Collider>();
        if (collider != null)
            collider.enabled = true;
    }

    public void OnSceneChanged()
    {
        ClearAllObjects();
        _availableShells = Array.Empty<ShellTypeCode>();
        _selectedShell = ShellTypeCode.HE;
        _availabilityKnown = false;
        _isChinese = false;
        _initialized = false;
        AutoFire = false;
        MaxCharge = false;
    }

    public void Dispose() => OnSceneChanged();

    private void RebuildShellButtons()
    {
        ClearShellButtons();

        if (_availableShells.Length == 0)
        {
            if (_availabilityKnown)
                MelonLogger.Warning("[FCS] Ammo UI: Requisition Console exposed no known shell types");
            return;
        }

        MelonLogger.Msg(
            $"[FCS] Ammo UI: showing {_availableShells.Length} available shell types, selected={ShellName(_selectedShell)}");

        for (var index = 0; index < _availableShells.Length; index++)
        {
            var shell = _availableShells[index];
            var captured = shell;
            var row = index < LeftAmmoCount
                ? index
                : RightAmmoStartRow + index - LeftAmmoCount;
            var z = index < LeftAmmoCount ? AmmoLeftZ : RightColumnZ;
            var x = UiStartX - row * UiRowX;
            var y = UiStartY - row * UiRowY;

            GameObject? button = null;
            button = AddButton(() =>
            {
                _selectedShell = captured;
                foreach (var item in _shellButtons)
                    SetColor(item, item == button ? Color.green : Color.white);
            }, shell == _selectedShell ? Color.green : Color.white);

            button.transform.position = new Vector3(x, y, z);
            button.transform.localScale = Vector3.one * 0.02f;
            _shellButtons.Add(button);

            var text = AddText(ShellName(shell), 14f);
            text.transform.SetParent(button.transform, false);
            text.transform.localPosition = new Vector3(-1.9f, 0, -10.6f);
            text.transform.localScale = Vector3.one;
        }
    }

    private void ClearShellButtons()
    {
        foreach (var button in _shellButtons)
        {
            if (button == null)
                continue;

            var collider = button.GetComponent<Collider>();
            if (collider != null)
                _clicks.Unregister(collider);

            var ownedObjects = button.GetComponentsInChildren<Transform>(true)
                .Select(transform => transform.gameObject)
                .ToArray();
            foreach (var ownedObject in ownedObjects)
                _ownedObjects.Remove(ownedObject);

            Object.Destroy(button);
        }
        _shellButtons.Clear();
    }

    private void InitializeTargetButtons()
    {
        var x = UiStartX;
        var y = UiStartY;
        var toggleFontSize = _isChinese ? 14f : 11f;

        GameObject? autoFireButton = null;
        autoFireButton = AddButton(() =>
        {
            AutoFire = !AutoFire;
            MelonLogger.Msg($"[FCS] AutoFire toggled {(AutoFire ? "ON" : "OFF")}");
            SetColor(autoFireButton!, AutoFire ? Color.red : Color.white);
            if (_autoFireLabel != null)
                _autoFireLabel.text = AutoFireText();
            if (AutoFire)
                _onAutoFireEnabled();
        }, AutoFire ? Color.red : Color.white);

        autoFireButton.transform.position = new Vector3(x, y, RightColumnZ);
        autoFireButton.transform.localScale = Vector3.one * 0.02f;
        var autoFireText = AddText(AutoFireText(), toggleFontSize);
        _autoFireLabel = autoFireText.GetComponent<TextMeshPro>();
        autoFireText.transform.SetParent(autoFireButton.transform, false);
        autoFireText.transform.localPosition = new Vector3(-1.9f, 0, -10.6f);
        autoFireText.transform.localScale = Vector3.one;

        x -= UiRowX;
        y -= UiRowY;

        GameObject? maxChargeButton = null;
        maxChargeButton = AddButton(() =>
        {
            MaxCharge = !MaxCharge;
            MelonLogger.Msg($"[FCS] MaxCharge toggled {(MaxCharge ? "ON" : "OFF")}");
            SetColor(maxChargeButton!, MaxCharge ? Color.red : Color.white);
            if (_maxChargeLabel != null)
                _maxChargeLabel.text = MaxChargeText();
        }, MaxCharge ? Color.red : Color.white);

        maxChargeButton.transform.position = new Vector3(x, y, RightColumnZ);
        maxChargeButton.transform.localScale = Vector3.one * 0.02f;
        var maxChargeText = AddText(MaxChargeText(), toggleFontSize);
        _maxChargeLabel = maxChargeText.GetComponent<TextMeshPro>();
        maxChargeText.transform.SetParent(maxChargeButton.transform, false);
        maxChargeText.transform.localPosition = new Vector3(-1.9f, 0, -10.6f);
        maxChargeText.transform.localScale = Vector3.one;

        x -= UiRowX;
        y -= UiRowY;

        for (var i = 1; i <= 4; i++)
        {
            var targetId = i;
            GameObject? button = null;
            button = AddButton(() =>
            {
                if (!_trySubmitTarget(targetId, _selectedShell))
                    return;

                SetColor(button!, Color.gray);
                var collider = button!.GetComponent<Collider>();
                if (collider != null)
                    collider.enabled = false;
            }, Color.red);

            button.transform.position = new Vector3(x, y, RightColumnZ);
            button.transform.localScale = Vector3.one * 0.02f;
            _targetButtons[targetId] = button;

            var text = AddText("T" + targetId, 14f);
            text.transform.SetParent(button.transform, false);
            text.transform.localPosition = new Vector3(-1.9f, 0, -10.6f);
            text.transform.localScale = Vector3.one;

            x -= UiRowX;
            y -= UiRowY;
        }
    }

    private void RefreshToggleLabels()
    {
        if (_autoFireLabel != null)
        {
            _autoFireLabel.text = AutoFireText();
            _autoFireLabel.fontSize = _isChinese ? 14f : 11f;
        }
        if (_maxChargeLabel != null)
        {
            _maxChargeLabel.text = MaxChargeText();
            _maxChargeLabel.fontSize = _isChinese ? 14f : 11f;
        }
    }

    private string AutoFireText() =>
        _isChinese ? $"自动开火：{OnOff(AutoFire)}" : $"Auto Fire: {OnOff(AutoFire)}";

    private string MaxChargeText() =>
        _isChinese ? $"最大装药：{OnOff(MaxCharge)}" : $"Max Charge: {OnOff(MaxCharge)}";

    private string OnOff(bool value) => _isChinese
        ? value ? "开" : "关"
        : value ? "ON" : "OFF";

    private static string ShellName(ShellTypeCode shell) => shell.ToString();

    private GameObject AddButton(Action onClick, Color color)
    {
        var button = GameObject.CreatePrimitive(PrimitiveType.Cube);
        _ownedObjects.Add(button);
        var collider = button.GetComponent<Collider>();
        _clicks.Register(collider, onClick);
        SetColor(button, color);
        return button;
    }

    private GameObject AddText(string text, float fontSize)
    {
        var go = new GameObject("FcsText");
        _ownedObjects.Add(go);
        go.transform.Rotate(new Vector3(90, 0, 0));
        go.transform.Rotate(new Vector3(0, 0, -90));
        var tmp = go.AddComponent<TextMeshPro>();
        if (tmp.font == null && TMP_Settings.defaultFontAsset != null)
            tmp.font = TMP_Settings.defaultFontAsset;
        tmp.text = text;
        tmp.fontSize = fontSize;
        tmp.color = Color.white;
        return go;
    }

    private static void SetColor(GameObject go, Color color)
    {
        var renderer = go.GetComponent<Renderer>();
        if (renderer == null)
            return;

        var shader = Shader.Find("Universal Render Pipeline/Unlit")
                     ?? Shader.Find("Universal Render Pipeline/Lit");
        if (shader == null)
        {
            if (renderer.material != null)
                renderer.material.color = color;
            return;
        }

        var mat = new Material(shader) { color = color };
        if (mat.HasProperty("_BaseColor"))
            mat.SetColor("_BaseColor", color);
        renderer.material = mat;
    }

    private void ClearAllObjects()
    {
        _clicks.Clear();
        foreach (var obj in _ownedObjects)
        {
            if (obj != null)
                Object.Destroy(obj);
        }
        _ownedObjects.Clear();
        _shellButtons.Clear();
        _targetButtons.Clear();
        _autoFireLabel = null;
        _maxChargeLabel = null;
    }
}
