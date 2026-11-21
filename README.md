# Telltale Format Manipulator

A Telltale Games Modding Tool

WIP (including readme), first release soon

Made in Unity 6000.6.8f1 to take full advantage of all of the official AI-integration tools. Final build made in Unity 2023.1.22f1, backported for Windows 7 support.

Currently supports:

Archives: dump names of ttarch archives as .txt or as a binary (supposedly all games).
Bones: manipulating bones inside of the .skl directly without the need to use a 3D editor, changing their names coordinates (supposedly all games).
Props: reading, editing and combining .prop file properties (TWDTTDS only).

It was entirely vide-coded using Claude Opus model.

TelltaleGames_D3DMesh by RandomTBrush (https://github.com/RandomTBush/RTB-3DSMax-Scripts) was used as reference to study the .skl format.

TelltaleToolKit by iMrShadow (https://github.com/iMrShadow/TelltaleToolKit) was used as reference to work with .ttarch/.ttarch2 archives and certain parts of .prop files.

BoneNames.HashDB file was taken from TelltaleGames_D3DMesh (renamed BoneNames.HashDB.bytes).

PropertySet-Keys.txt and global.vdb.json from TelltaleToolKit were used to create lists of names.

Everything else here is made by Claude.