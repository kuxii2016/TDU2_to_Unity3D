using System;
using System.IO;
using TDU2.Flash;
using TDU2.Racing;
using TDU2.Vehicles;
using UnityEngine;

public class TDU2TDUMedia : MonoBehaviour
{
    public bool UseOriginalFilm = true;
    public TDU2VehicleManager Manager;
    VmfPlayer _film;
    string _bank, _bnkDir;
    RaceRepository _repo;

    // Start is called once before the first execution of Update after the MonoBehaviour is created
    void Start()
    {

    }

    public void Open()
    {
        LoadFilm();
    }

    // Update is called once per frame
    void Update()
    {
        if (Input.GetKeyDown(KeyCode.UpArrow) || Input.GetKeyDown(KeyCode.W)) _film.Input("Move", "up");
        if (Input.GetKeyDown(KeyCode.DownArrow) || Input.GetKeyDown(KeyCode.S)) _film.Input("Move", "down");
        if (Input.GetKeyDown(KeyCode.LeftArrow) || Input.GetKeyDown(KeyCode.A)) _film.Input("Move", "left");
        if (Input.GetKeyDown(KeyCode.RightArrow) || Input.GetKeyDown(KeyCode.D)) _film.Input("Move", "right");
        if (Input.GetKeyDown(KeyCode.Return) || Input.GetKeyDown(KeyCode.KeypadEnter) || Input.GetKeyDown(KeyCode.Space))
        {
            if (!_film.Input("A")) _film.CallClip("/", "Open");   // auf der Schulübersicht: Lizenzrad wieder öffnen
        }
        if (Input.GetKeyDown(KeyCode.Escape) || Input.GetKeyDown(KeyCode.Backspace))
        {
            if (!_film.Input("B")) Close();
        }
    }

    private void Close()
    {
    }

    void LoadFilm()
    {
        if (!UseOriginalFilm || _film != null || Manager == null || string.IsNullOrEmpty(Manager.BnkDir)) return;
        string bank = null;
        foreach (var res in new[] { "hires", "medres", "lowres" })
        {
            bank = CarDatabase.FindFile(Path.Combine(Manager.BnkDir, "frontend", res), "tdmedia.bnk");
            if (bank != null) break;
        }
        if (bank == null) { Debug.LogWarning("tdmedia.bnk nicht gefunden"); return; }
        try
        {
            var go = new GameObject("TDU2 tdmedia UI");
            go.transform.SetParent(transform, false);
            _film = go.AddComponent<VmfPlayer>();
            _film.GuiDepth = -2;
            _film.MouseEnabled = true;
            _film.Load(bank);
            _film.Visible = false;
            _film.FSCommand += OnFilmCommand;
            _bank = bank;


            _film.SetValue("/:WhatPlatform", 1);
            _film.SetVariable("/:MenuType", "pc");
            _film.SetValue("/:IsPC_X360", true);
            _film.SetValue("/:fake", false);
            _film.SetValue("/:Input", true);
            _film.SetValue("/:IsMyHouse", true);

            FlashLocalization.Apply(_film, FlashLocalization.Load(Manager.BnkDir, TDU2PauseMenu.SavedLanguage()));
        }
        catch (Exception ex)
        {
            Debug.LogWarning("tdmedia.bnk: " + ex.Message);
            if (_film != null) { Destroy(_film.gameObject); _film = null; }
        }
    }

    private void OnFilmCommand(string cmd, string def)
    {
        DebugManager.Warning("Missing Command: " + cmd + " def: " +def);
        string c = (cmd ?? "").Trim().ToLowerInvariant();
        if (TDU2FrontendSounds.Command(c)) return;
    }
}
