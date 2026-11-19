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

TelltaleToolKit by iMrShadow (https://github.com/iMrShadow/TelltaleToolKit) was used as reference to work with .ttarch/.ttarch2 archives.

BoneNames.HashDB file was taken from TelltaleGames_D3DMesh (renamed BoneNames.HashDB.bytes).

PropertySet-Keys.txt and global.vdb.json from TelltaleToolKit were used to create lists of names.

Everything else here is made by Claude.



> Compare the sk63_omar_bodyUpper_m / _wet_m and sk63_omar_bodyLower_m / _wet_m files quickly, tell me if they are of any use

I'll compare the two pairs byte by byte.

  Ran 1 shell command

The two pairs are not near-identical: about 7,800 of 10,600 bytes differ, and the sizes don't match either. I'll check whether that's a real content difference or just an offset shift.

  Ran 1 shell command

The headers differ, but partly because of the byte at offset 4, which is the file length field. I'll compare only the parts that are layout, ignoring length and CRC.

  Thought for 18s, ran 1 shell command

The upper pair shares an identical type table and differs in its parent prop. That makes it the more promising pair, so I'll compare its payloads field by field.