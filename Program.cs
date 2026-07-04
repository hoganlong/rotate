using Amazon.S3;
using Amazon.S3.Model;
using ImageMagick;
using Microsoft.Extensions.Configuration;
using System.Diagnostics;

class Program
{
  static void PrintUsage()
  {
    Console.WriteLine("Usage: dotnet run -- <s3uri> [--angle 90|180|270] [--upload]");
    Console.WriteLine();
    Console.WriteLine("Downloads a JPG or TIF file from S3, rotates it clockwise by the given angle");
    Console.WriteLine("losslessly, and writes the rotated copy to the current directory as");
    Console.WriteLine("rot<angle>_<filename>. With --upload, also overwrites the original S3 key with");
    Console.WriteLine("the rotated bytes.");
    Console.WriteLine();
    Console.WriteLine("  JPG  → bundled jpegtran.exe (DCT block transform; zero quality loss)");
    Console.WriteLine("  TIF  → Magick.NET Rotate(angle) (TIF is lossless by definition)");
    Console.WriteLine();
    Console.WriteLine("Positional:");
    Console.WriteLine("  <s3uri>                 s3://bucket/path/to/file.{jpg|jpeg|tif|tiff}");
    Console.WriteLine();
    Console.WriteLine("Options:");
    Console.WriteLine("  --angle <90|180|270>    clockwise rotation angle (default: 180)");
    Console.WriteLine("  --upload                overwrite the original S3 key with the rotated file");
    Console.WriteLine("  -h, --help, -?, /?, ?   show this help and exit");
    Console.WriteLine();
    Console.WriteLine("Configuration (appsettings.json):");
    Console.WriteLine("  S3:Region               AWS region (default: us-east-1)");
    Console.WriteLine("  Jpegtran:Path           path to jpegtran.exe (default: ./jpegtran.exe");
    Console.WriteLine("                          next to the running EXE)");
  }

  static async Task<int> Main(string[] args)
  {
    if (args.Any(a => a is "-h" or "--help" or "-?" or "/?" or "?"))
    {
      PrintUsage();
      return 0;
    }

    bool upload = false;
    int angle = 180;
    var positional = new List<string>();
    for (int i = 0; i < args.Length; i++)
    {
      var a = args[i];
      if (a == "--upload") upload = true;
      else if (a == "--angle")
      {
        if (i + 1 >= args.Length)
        {
          Console.WriteLine("✗ --angle requires a value (90, 180, or 270).");
          Console.WriteLine();
          PrintUsage();
          return 1;
        }
        var val = args[++i];
        if (!int.TryParse(val, out angle) || (angle != 90 && angle != 180 && angle != 270))
        {
          Console.WriteLine($"✗ Invalid --angle value: {val}. Only 90, 180, or 270 are supported.");
          Console.WriteLine();
          PrintUsage();
          return 1;
        }
      }
      else if (a.StartsWith("-") || a.StartsWith("/"))
      {
        Console.WriteLine($"Unknown option: {a}");
        Console.WriteLine();
        PrintUsage();
        return 1;
      }
      else positional.Add(a);
    }

    if (positional.Count < 1)
    {
      PrintUsage();
      return 1;
    }
    var s3Uri = positional[0];

    var configuration = new ConfigurationBuilder()
      .SetBasePath(AppContext.BaseDirectory)
      .AddJsonFile("appsettings.json", optional: true)
      .Build();

    var region = configuration["S3:Region"] ?? "us-east-1";
    var jpegtranPath = configuration["Jpegtran:Path"];
    if (string.IsNullOrWhiteSpace(jpegtranPath))
      jpegtranPath = Path.Combine(AppContext.BaseDirectory, "jpegtran.exe");

    Console.WriteLine("╔════════════════════════════════════════════════════════════╗");
    Console.WriteLine("║                rotate180 (S3, lossless)                    ║");
    Console.WriteLine("╚════════════════════════════════════════════════════════════╝");
    Console.WriteLine();
    Console.WriteLine($"S3 URI    : {s3Uri}");
    Console.WriteLine($"Region    : {region}");
    Console.WriteLine($"Angle     : {angle}° clockwise");
    Console.WriteLine($"Mode      : {(upload ? "download + rotate + overwrite original S3 key" : "download + rotate (local only)")}");
    Console.WriteLine();

    if (!s3Uri.StartsWith("s3://"))
    {
      Console.WriteLine($"✗ S3 URI must start with s3://  Got: {s3Uri}");
      return 1;
    }

    var (bucket, key) = ParseS3Uri(s3Uri);
    var filename = Path.GetFileName(key);
    if (string.IsNullOrEmpty(filename))
    {
      Console.WriteLine($"✗ S3 URI must point at a file (got a prefix): {s3Uri}");
      return 1;
    }
    var ext = Path.GetExtension(filename).ToLowerInvariant();
    bool isJpg = ext is ".jpg" or ".jpeg";
    bool isTif = ext is ".tif" or ".tiff";
    if (!isJpg && !isTif)
    {
      Console.WriteLine($"✗ Unsupported file extension: {ext}. Only .jpg/.jpeg/.tif/.tiff are supported.");
      return 1;
    }

    var localIn = Path.Combine(Path.GetTempPath(), $"rot{angle}-in-{Guid.NewGuid():N}{ext}");
    var localOut = Path.Combine(Directory.GetCurrentDirectory(), $"rot{angle}_{filename}");

    var s3Client = new AmazonS3Client(Amazon.RegionEndpoint.GetBySystemName(region));

    try
    {
      Console.WriteLine($"↓ Downloading s3://{bucket}/{key}");
      using (var response = await s3Client.GetObjectAsync(new GetObjectRequest { BucketName = bucket, Key = key }))
      await using (var fs = File.Create(localIn))
        await response.ResponseStream.CopyToAsync(fs);
      var inSize = new FileInfo(localIn).Length;
      Console.WriteLine($"  ✓ {inSize:N0} bytes");

      Console.WriteLine($"↻ Rotating {angle}° clockwise");
      if (isJpg)
      {
        if (!File.Exists(jpegtranPath))
        {
          Console.WriteLine($"✗ jpegtran.exe not found at: {jpegtranPath}");
          Console.WriteLine("  Bundle jpegtran.exe (from libjpeg-turbo: https://libjpeg-turbo.org/)");
          Console.WriteLine("  next to the running EXE, or set Jpegtran:Path in appsettings.json.");
          return 1;
        }
        var psi = new ProcessStartInfo
        {
          FileName = jpegtranPath,
          UseShellExecute = false,
          RedirectStandardError = true,
          CreateNoWindow = true,
        };
        psi.ArgumentList.Add("-rotate"); psi.ArgumentList.Add(angle.ToString());
        // Drop the partial edge strip that can't be transformed losslessly when
        // the image dimensions aren't a multiple of the MCU block size (8/16px).
        // No-op for evenly-divisible images.
        psi.ArgumentList.Add("-trim");
        psi.ArgumentList.Add("-copy"); psi.ArgumentList.Add("all");
        psi.ArgumentList.Add("-outfile"); psi.ArgumentList.Add(localOut);
        psi.ArgumentList.Add(localIn);
        using var proc = Process.Start(psi)!;
        var stderr = await proc.StandardError.ReadToEndAsync();
        await proc.WaitForExitAsync();
        if (proc.ExitCode != 0)
        {
          Console.WriteLine($"✗ jpegtran exited {proc.ExitCode}: {stderr.Trim()}");
          return 1;
        }
      }
      else // TIF
      {
        using var img = new MagickImage(localIn);
        // Bake any existing EXIF/TIFF orientation tag into the pixels and reset
        // it to normal, so our rotation isn't stacked on top of a tag the viewer
        // also applies. No-op when the image has no orientation (tag 1/undefined).
        img.AutoOrient();
        img.Rotate(angle);
        img.Write(localOut);
      }

      var outSize = new FileInfo(localOut).Length;
      Console.WriteLine($"  ✓ Wrote {localOut}  ({outSize:N0} bytes)");

      if (upload)
      {
        Console.WriteLine($"↑ Uploading to s3://{bucket}/{key}");
        await s3Client.PutObjectAsync(new PutObjectRequest
        {
          BucketName = bucket,
          Key = key,
          FilePath = localOut,
          ContentType = isJpg ? "image/jpeg" : "image/tiff"
        });
        Console.WriteLine("  ✓ Replaced original key");
      }
      else
      {
        Console.WriteLine();
        Console.WriteLine("(Re-run with --upload to overwrite the original S3 key.)");
      }
      return 0;
    }
    catch (AmazonS3Exception ex)
    {
      Console.WriteLine($"✗ AWS S3 Error: {ex.Message}");
      Console.WriteLine($"  Error Code: {ex.ErrorCode}");
      return 1;
    }
    catch (Exception ex)
    {
      Console.WriteLine($"✗ Error: {ex.Message}");
      return 1;
    }
    finally
    {
      if (File.Exists(localIn))
      {
        try { File.Delete(localIn); } catch { /* best effort */ }
      }
    }
  }

  static (string bucket, string key) ParseS3Uri(string s3Uri)
  {
    var withoutScheme = s3Uri.Substring(5);
    var slash = withoutScheme.IndexOf('/');
    if (slash < 0) throw new Exception($"S3 URI must include a key: {s3Uri}");
    return (withoutScheme.Substring(0, slash), withoutScheme.Substring(slash + 1));
  }
}
