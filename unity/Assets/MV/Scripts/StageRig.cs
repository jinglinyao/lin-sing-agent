using UnityEngine;

/// <summary>
/// The stage set - reusable (Assets/MV/Props/Stage.prefab, built by the menu "MV / Build Props"): a dark floor that takes her shadow, a soft
/// glow on a big card behind her whose colour follows the song, dust drifting up through the light. The capture / live player (FrameDumper)
/// drives the colours and keeps the card behind her as seen from the running camera; without it the set simply stands as saved.
/// </summary>
public class StageRig : MonoBehaviour
{
    public Renderer glow, floor;
    public ParticleSystem dust;
    public Light key;               // the scene's key light (set in the scene, not in the prefab)
}
