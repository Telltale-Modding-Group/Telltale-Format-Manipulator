# Telltale Format Manipulator

A Telltale Games Modding Tool

WIP (including readme)

Made in Unity 6000.6.8f1 to take full advantage of all of the official AI-integration tools. Final build made in Unity 2023.1.22f1, backported for Windows 7 support.

Currently supports:

Archives: dump names of ttarch archives as .txt or as a binary (supposedly all games).

Bones: manipulating bones inside of the .skl directly without the need to use a 3D editor, changing their names coordinates (supposedly all games).

Props: reading, editing and combining .prop file properties (TWDTTDS only).

Chores: reading and editing (TWDTTDS only).

Dialogues: conversation nodes, reading and editing (TWDTTDS only).


It was entirely vide-coded using Claude Opus model.

TelltaleGames_D3DMesh by RandomTBrush (https://github.com/RandomTBush/RTB-3DSMax-Scripts) was used as reference to study the .skl format. BoneNames.HashDB file was taken from TelltaleGames_D3DMesh (renamed BoneNames.HashDB.bytes).

TelltaleToolKit by iMrShadow (https://github.com/iMrShadow/TelltaleToolKit) was heavily used as part of this tool's development and format investigation.

Everything else here is made by Claude.

Notes:

Master Bank.strings.bank and *.soundeventbankmap may contain FMOD references that are currently missing from chore editor, need a solution.

logic_game*.prop files in each episode contain game logic hashes, but no strings.

The stirngs for game logic must be guessed or brute-forced or found in lua. Lua files such as NextTimeOn and statistics seem to contain a number of useful references.
