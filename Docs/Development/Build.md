# Сборка, установка и ресурсы

[Документация](../README.md) · [Репозиторий](../../README.md)

Команды ниже выполняются из корня репозитория.

## Готовые моды

Перенесённые сборки находятся в `Builds/RimVision`, `Builds/RimVision-Multiplayer-Compat`, `Builds/RimVision-Mod-Compatibility` и `Builds/RimVision-ThirdPerson`. Для установки скопируйте нужную папку в `RimWorld/Mods`. Аддоны включаются после основного мода. Основной мод требует Harmony; Multiplayer-аддон также требует Multiplayer.

## Сборка без Unity Editor

Нужны Windows, .NET SDK с поддержкой `net472`, PowerShell 7.2+, установленный RimWorld и Harmony. Для Multiplayer-аддона также нужен установленный Multiplayer. DLL игры читаются из установки RimWorld и не входят в репозиторий.

Для запуска двойным щелчком используйте `Build.bat`. Он запускает `Build.ps1` и оставляет окно с результатом открытым. Параметры также можно передавать батке, например `Build.bat -Mod Core`.

Локальная `start.bat` собирает все четыре мода, устанавливает их в `D:/SteamLibrary/steamapps/common/RimWorld/Mods` и после успешной установки запускает RimWorld. При ошибке сборки или установки игра не запускается. Перед запуском закройте уже открытый RimWorld. Другой путь игры: `start.bat -GameRoot "путь/RimWorld"`. Батка исключена из Git. Предыдущие установленные папки сохраняются в `RimWorld/RimVisionBackups`, существующие `PublishedFileId.txt` сохраняются при установке. Другие моды в игре не затрагиваются. Используется готовый shader bundle; изменённые шейдеры сначала соберите через `BuildShaders.local.bat`.

```powershell
pwsh -File ./Build.ps1 -GameRoot 'D:/SteamLibrary/steamapps/common/RimWorld'
pwsh -File ./Build.ps1 -Mod Core -GameRoot 'D:/SteamLibrary/steamapps/common/RimWorld'
```

`-Mod` также принимает `Multiplayer`, `Compatibility`, `ThirdPerson`. Путь к Harmony и Multiplayer можно передать через `-HarmonyPath` и `-MultiplayerRoot`, путь к .NET –  через `-DotNet`.

`Build.ps1` (в том числе через `Build.bat` и `start.bat`) сначала ищет Harmony в `RimWorld/Mods/HarmonyMod/Current/Assemblies/0Harmony.dll` или подпапке `1.6/Assemblies`, а Multiplayer –  в `RimWorld/Mods/Multiplayer/Assemblies/0MultiplayerAPI.dll` или подпапке `1.6/Assemblies`. Если локальной копии нет, используются подписки Steam Workshop из той же библиотеки, где находится игра: `steamapps/workshop/content/294100/2009463077/1.6/Assemblies/0Harmony.dll` и `steamapps/workshop/content/294100/2606448745/1.6/Assemblies/0MultiplayerAPI.dll` (также поддерживаются прежняя папка `Current` у Harmony и `Assemblies` в корне Multiplayer). Копировать эти моды в папку игры не требуется. Явно заданные пути имеют приоритет; для другой библиотеки Steam укажите их вручную. `-MultiplayerRoot` должен указывать на папку, непосредственно содержащую `Assemblies`.

`Version.txt` задаёт первую строку описания основного мода и Multiplayer-аддона на английском и русском языках. Запишите нужную версию одной строкой, включая `v`, если он нужен, и пересоберите моды. Значение переносится в описания собранных пакетов; русское описание Multiplayer получает его из ресурса DLL.

Скрипт компилирует DLL, копирует ресурсы, генерирует прежние LOD только в staging, добавляет API-документацию и создаёт папки модов и ZIP в `Builds`. Зависимые аддоны компилируются с актуальным основным проектом; DLL основного мода не попадает внутрь аддонов. Предыдущие выбранные сборки сохраняются в `Builds/.previous`. При ошибке staging остаётся для разбора. Установка выполняется через `start.bat`, параметр `-Install` у `Build.ps1` либо вручную.

Отдельная компиляция без упаковки:

При прямом вызове `dotnet build` автоматический поиск Workshop не выполняется: при необходимости передайте `-p:HarmonyPath='путь/0Harmony.dll'` и для Multiplayer-проекта `-p:MultiplayerRoot='путь/папка-с-Assemblies'`.

```powershell
dotnet build Source/RimVision/Rim3D.csproj -c Release -p:GameRoot='D:/SteamLibrary/steamapps/common/RimWorld'
```

### Шейдеры

Для обычной сборки используется `Mods/RimVision/ShaderBundles/rim3d-shaders`; Unity Editor не запускается. Изменение файлов в `Shaders` само по себе не меняет этот bundle. Зависимость самого мода от UnityEngine сохраняется: это движок RimWorld, его DLL берутся из игры.

Локальный `BuildShaders.local.ps1` пересобирает bundle через Unity 2022.3.35f1 в фоновом режиме и обновляет его в `Mods/RimVision/ShaderBundles`. Для запуска двойным щелчком используйте `BuildShaders.local.bat`. Из консоли: `BuildShaders.local.bat -UnityEditor "путь/Editor/Unity.exe"`, если редактор находится в другом месте. Затем запустите `Build.bat` для упаковки мода. Старый Unity-проект не нужен: минимальный служебный проект, кэш, логи и предыдущий bundle сохраняются в `.shader-build`. Оба локальных скрипта шейдеров и служебная папка исключены из Git; они доступны только в этой локальной копии.

## Blender и модели

Инструкции: [руководство Blender-плагина](../../Tools/Blender/README.md). Упаковка: `python Tools/Blender/package.py`; для Blender Extensions –  с `--extension`. Скрипты генерации моделей в `Tools/Models` изменяют `Mods/RimVision/OptionalContent`. Генератор `GenerateSimpleProps.py` читает Defs из установленной игры; переменная `RIMWORLD_DATA` задаёт путь к `RimWorld/Data`.
