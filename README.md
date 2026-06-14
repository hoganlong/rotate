# rotate180

A .NET 10 console tool that downloads an image from S3, rotates it 180° **losslessly**, writes the result to the current directory, and (optionally) overwrites the original S3 object with the rotated bytes.

- **JPG** is rotated via the bundled `jpegtran.exe` (libjpeg-turbo). 180° rotation is a pure DCT-block transform — the JPEG is never re-encoded, so there is zero quality loss. EXIF/IPTC/ICC metadata is preserved.
- **TIF** is rotated via Magick.NET. TIFF is lossless by definition, so a decode-rotate-encode round-trip does not degrade the image.

---

## Usage

```bash
dotnet run -- s3://bucket/path/to/file.jpg            # download + rotate; write rot180_file.jpg locally
dotnet run -- s3://bucket/path/to/file.tif --upload   # ... and overwrite the original S3 key

dotnet run -- --help                                  # also: -h, -?, /?, ?
```

Without `--upload`, the rotated file is written locally as `rot180_<filename>` in the current directory and the original S3 object is left untouched. Re-run with `--upload` after inspecting the local file.

`--upload` overwrites the original S3 key. There is no automatic backup — make one yourself if you need it (e.g., `aws s3 cp s3://bucket/foo.jpg s3://bucket/foo.jpg.bak` before rotating).

Supported extensions: `.jpg`, `.jpeg`, `.tif`, `.tiff`. Anything else is rejected.

---

## Setup

### 1. Bundle `jpegtran.exe` (and its runtime DLL)

Download libjpeg-turbo for Windows from <https://libjpeg-turbo.org/> (or its GitHub releases page). From the install's `bin\` folder, copy **both** of these into this project folder (next to `rotate180.csproj`):

- `jpegtran.exe`
- the libjpeg-turbo runtime DLL — named **`jpeg62.dll`** in MSVC/CMake builds, or **`libjpeg-62.dll`** in MinGW/autotools builds. Copy whichever your install ships.

The dynamically linked Windows build of `jpegtran.exe` is ~57 KB and depends on that DLL (~250–400 KB) at runtime. Without it, `jpegtran.exe` fails to start with an error about the missing DLL. The csproj copies both filename variants to the build output if they're present, so you don't have to rename.

You can verify by opening `cmd`, `cd`-ing to this folder, and running `jpegtran.exe -version` — it should print a version string instead of erroring.

Alternatively, point `Jpegtran:Path` in `appsettings.json` at an existing libjpeg-turbo install (e.g. `C:\\libjpeg-turbo64\\bin\\jpegtran.exe`); the DLL will be picked up from that install's `bin\` folder automatically.

### 2. Configure (optional)

Copy `appsettings.template.json` to `appsettings.json` and adjust:

```json
{
  "S3": { "Region": "us-east-1" },
  "Jpegtran": { "Path": "" }
}
```

- `S3:Region` — AWS region of the bucket. Defaults to `us-east-1` if unset.
- `Jpegtran:Path` — absolute path to `jpegtran.exe`. Defaults to `./jpegtran.exe` next to the running EXE.

AWS credentials are read from the standard credential chain (environment variables, `~/.aws/credentials`, IAM role, etc.).

### 3. Run

```bash
cd rotate180
dotnet run -- s3://my-bucket/photos/IMG_1234.jpg --upload
```

---

## How it works

1. Parse the S3 URI into `bucket` + `key`.
2. Download the object to a temp file.
3. Branch on extension:
   - **JPG** → invoke `jpegtran -rotate 180 -copy all -outfile <localOut> <localIn>`.
   - **TIF** → `new MagickImage(localIn).Rotate(180)` then `.Write(localOut)`.
4. Save the rotated copy as `rot180_<filename>` in the current directory.
5. If `--upload` was passed, `PutObject` the rotated bytes back to the original S3 key.
6. Delete the input temp file.

The local `rot180_<filename>` is left on disk so you can inspect it; it's also gitignored.

---

## Notes

- 180° rotation is one of the special angles (0°/90°/180°/270°) for which JPEG lossless transforms are unconditionally safe. For non-multiples-of-90 angles you'd have to decode and re-encode — out of scope here.
- The tool is non-interactive and exits after one file. Loop in a shell script if you need to rotate many keys.
