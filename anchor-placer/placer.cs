// SimpleAnchorPlacer.cs — one-script object placement with persistent spatial anchors.
// Put on any GameObject. Fill the Inventory list. Prefabs need a Collider.
//
// Right A = spawn   Right B = next item   Grip = grab/move (release saves)
// Left X  = delete last touched          Left Y = clear all

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using TMPro;
using UnityEngine;

public class SimpleAnchorPlacer : MonoBehaviour
{
    [Serializable] public class Item { public string id; public GameObject prefab; }

    public List<Item> inventory = new List<Item>();
    public TMP_Text status;            // optional; auto-found if empty
    public float grabRadius = 0.1f;
    public float spawnDistance = 0.6f;

    // Save file format
    [Serializable] class Record { public string uuid; public string id; public Vector3 scale; }
    [Serializable] class SaveFile { public List<Record> items = new List<Record>(); }

    // One per placed object
    class Placed { public GameObject go; public string id; public Guid uuid; public OVRSpatialAnchor anchor; public bool busy; }

    // One per controller
    class Hand { public OVRInput.Controller ctrl; public Transform t; public Placed held; public Vector3 pos; public Quaternion rot; }

    readonly List<Placed> placed = new List<Placed>();
    List<Record> unrestored = new List<Record>(); // saved but not loaded this session; kept in the file
    Hand left, right;
    Placed lastTouched;
    int selected;
    string path;
    Transform head;

    async void Start()
    {
        var rig = FindAnyObjectByType<OVRCameraRig>();
        head = rig.centerEyeAnchor;
        left = new Hand { ctrl = OVRInput.Controller.LTouch, t = rig.leftControllerAnchor };
        right = new Hand { ctrl = OVRInput.Controller.RTouch, t = rig.rightControllerAnchor };
        if (!status) status = FindAnyObjectByType<TMP_Text>();
        path = Path.Combine(Application.persistentDataPath, "anchors.json");

        // ---- Load on startup: load by UUID -> localize -> bind ----
        if (!File.Exists(path)) { Say("Nothing saved. A = spawn, B = next item"); return; }

        var records = JsonUtility.FromJson<SaveFile>(File.ReadAllText(path)).items
                                 .ToDictionary(r => Guid.Parse(r.uuid));
        Say($"Loading {records.Count} objects...");

        var unbound = new List<OVRSpatialAnchor.UnboundAnchor>();
        var result = await OVRSpatialAnchor.LoadUnboundAnchorsAsync(records.Keys.ToList(), unbound);
        if (!result.Success) { Say("Load failed: " + result.Status); unrestored = records.Values.ToList(); return; }

        int ok = 0;
        foreach (var u in unbound)
        {
            if (!await u.LocalizeAsync()) continue;               // not found in this space; stays in file
            var rec = records[u.Uuid];
            var item = inventory.Find(i => i.id == rec.id);
            if (item == null) continue;                          // prefab ID no longer in inventory

            var go = Instantiate(item.prefab);
            go.transform.localScale = rec.scale;
            var anchor = go.AddComponent<OVRSpatialAnchor>();
            u.BindTo(anchor);                                    // same frame as AddComponent, or a new anchor gets made
            placed.Add(new Placed { go = go, id = rec.id, uuid = u.Uuid, anchor = anchor });
            records.Remove(u.Uuid);
            ok++;
        }
        unrestored = records.Values.ToList();
        Say(unrestored.Count == 0 ? $"Restored {ok} objects" : $"Restored {ok}, {unrestored.Count} not found (look around & restart)");
    }

    void Update()
    {
        if (head == null) return;
        var R = OVRInput.Controller.RTouch; var L = OVRInput.Controller.LTouch;

        if (OVRInput.GetDown(OVRInput.Button.One, R)) Spawn();
        if (OVRInput.GetDown(OVRInput.Button.Two, R) && inventory.Count > 0)
        {
            selected = (selected + 1) % inventory.Count;
            Say("Selected: " + inventory[selected].id);
        }
        if (OVRInput.GetDown(OVRInput.Button.One, L)) Delete(lastTouched);
        if (OVRInput.GetDown(OVRInput.Button.Two, L)) ClearAll();

        UpdateHand(left);
        UpdateHand(right);
    }

    void Spawn()
    {
        if (inventory.Count == 0) { Say("Inventory is empty"); return; }
        Vector3 fwd = Vector3.ProjectOnPlane(head.forward, Vector3.up).normalized;
        var go = Instantiate(inventory[selected].prefab, head.position + fwd * spawnDistance, Quaternion.LookRotation(-fwd));
        var p = new Placed { go = go, id = inventory[selected].id };
        placed.Add(p);
        lastTouched = p;
        Lock(p);
    }

    // ---- Grab / move ----
    void UpdateHand(Hand h)
    {
        if (OVRInput.GetDown(OVRInput.Button.PrimaryHandTrigger, h.ctrl) && h.held == null)
        {
            var p = FindNearest(h.t.position);
            if (p == null || p.busy) return;
            if (p.anchor) Destroy(p.anchor);   // anchor would fight the move; saved copy stays until re-save
            p.anchor = null;
            h.held = p; lastTouched = p;
            h.pos = h.t.InverseTransformPoint(p.go.transform.position);
            h.rot = Quaternion.Inverse(h.t.rotation) * p.go.transform.rotation;
        }

        if (h.held == null) return;
        if (h.held.go == null) { h.held = null; return; }   // deleted while held

        if (OVRInput.GetUp(OVRInput.Button.PrimaryHandTrigger, h.ctrl))
        {
            Lock(h.held);
            h.held = null;
            return;
        }
        h.held.go.transform.SetPositionAndRotation(h.t.TransformPoint(h.pos), h.t.rotation * h.rot);
    }

    Placed FindNearest(Vector3 pos)
    {
        foreach (var c in Physics.OverlapSphere(pos, grabRadius))
        {
            var p = placed.Find(x => x.go && c.transform.IsChildOf(x.go.transform));
            if (p != null && left.held != p && right.held != p) return p;
        }
        return null;
    }

    // ---- Create + save anchor, erase the old one ----
    async void Lock(Placed p)
    {
        p.busy = true;
        var anchor = p.go.AddComponent<OVRSpatialAnchor>();
        while (anchor && !anchor.Created) await Awaitable.NextFrameAsync();
        if (!anchor || !p.go) { p.busy = false; Say("Anchor creation failed"); return; }

        var result = await anchor.SaveAnchorAsync();
        p.busy = false;
        if (!result.Success) { Destroy(anchor); Say("Save failed: " + result.Status); return; }

        Guid old = p.uuid;
        p.anchor = anchor;
        p.uuid = anchor.Uuid;
        WriteFile();
        Say("Saved " + p.id);
        if (old != Guid.Empty) await OVRSpatialAnchor.EraseAnchorsAsync((IEnumerable<OVRSpatialAnchor>)null, new[] { old });
    }

    // ---- Delete / clear ----
    async void Delete(Placed p)
    {
        if (p == null || p.busy || !p.go) { Say("Nothing to delete (grab something first)"); return; }
        placed.Remove(p);
        Destroy(p.go);
        lastTouched = null;
        WriteFile();
        Say("Deleted " + p.id);
        if (p.uuid != Guid.Empty) await OVRSpatialAnchor.EraseAnchorsAsync((IEnumerable<OVRSpatialAnchor>)null, new[] { p.uuid });
    }

    async void ClearAll()
    {
        var uuids = placed.Where(p => p.uuid != Guid.Empty).Select(p => p.uuid)
                          .Concat(unrestored.Select(r => Guid.Parse(r.uuid))).ToList();
        foreach (var p in placed) if (p.go) Destroy(p.go);
        placed.Clear(); unrestored.Clear(); lastTouched = null;
        if (File.Exists(path)) File.Delete(path);
        Say("Cleared all");
        if (uuids.Count > 0) await OVRSpatialAnchor.EraseAnchorsAsync((IEnumerable<OVRSpatialAnchor>)null, uuids);
    }

    // ---- JSON ----
    void WriteFile()
    {
        var file = new SaveFile();
        foreach (var p in placed)
            if (p.uuid != Guid.Empty && p.go)
                file.items.Add(new Record { uuid = p.uuid.ToString(), id = p.id, scale = p.go.transform.localScale });
        file.items.AddRange(unrestored);
        File.WriteAllText(path, JsonUtility.ToJson(file, true));
    }

    void Say(string msg)
    {
        Debug.Log("[Placer] " + msg);
        if (status) status.text = msg;
    }
}