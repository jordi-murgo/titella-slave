// On-demand capture of the interactive Windows desktop with the permissions
// of the agent's own session: one native-resolution PNG of the primary
// monitor, origin (0,0). The bytes travel in the fs stream and live only in
// memory; no file is written on the slave and nothing runs between calls.
//
// The capture is best effort by nature: a locked session, a secure desktop,
// a protected surface or a disconnected RDP session is not guaranteed to be
// capturable. A failure is reported with its real diagnostic; a valid black
// PNG is not evidence of any particular cause.

namespace Titella.Slave
{
    using System;
    using System.Collections.Generic;
    using System.Drawing;
    using System.Drawing.Imaging;
    using System.Globalization;
    using System.IO;
    using System.Runtime.InteropServices;

    internal static class ScreenLimits
    {
        // Memory guard for the encoded PNG, not a promise that every image
        // under the ceiling crosses the link inside the master deadline.
        public const int MaxPngBytes = 32 * 1024 * 1024;
    }

    /// <summary>Primary-monitor capture served by fs.screen.</summary>
    internal sealed class ScreenService
    {
        /// <summary>
        /// Captures the primary monitor in its physical pixels and returns the
        /// PNG as the payload. The calling thread keeps the per-monitor DPI
        /// context only for the duration of the call.
        /// </summary>
        public FileService.Result Capture()
        {
            IntPtr previous = NativeMethods.SetThreadDpiAwarenessContext(
                Win32Constants.DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
            if (previous == IntPtr.Zero)
            {
                throw Failure(
                    "the thread DPI awareness context cannot be established",
                    Marshal.GetLastWin32Error());
            }
            FileService.Result result = null;
            try
            {
                result = CaptureInContext();
            }
            finally
            {
                // Restoring a different context would silently rescale the
                // next capture, so a failed restore is reported as a capture
                // failure unless another error is already on its way out.
                if (NativeMethods.SetThreadDpiAwarenessContext(previous) == IntPtr.Zero
                    && result != null)
                {
                    throw Failure(
                        "the previous thread DPI awareness context cannot be restored",
                        Marshal.GetLastWin32Error());
                }
            }
            return result;
        }

        private static FileService.Result CaptureInContext()
        {
            int width = NativeMethods.GetSystemMetrics(Win32Constants.SM_CXSCREEN);
            int height = NativeMethods.GetSystemMetrics(Win32Constants.SM_CYSCREEN);
            long pixels = (long)width * (long)height;
            if (width <= 0 || height <= 0 || pixels <= 0 || pixels > int.MaxValue)
            {
                throw Failure(
                    "the primary monitor reports no usable area (" +
                    Number(width) + "x" + Number(height) + ")",
                    0);
            }
            byte[] png = Encode(width, height);
            IDictionary<string, object> metadata = new Dictionary<string, object>();
            metadata["format"] = "png";
            metadata["width_px"] = width;
            metadata["height_px"] = height;
            metadata["bytes"] = png.Length;
            metadata["source"] = "agent";
            metadata["monitor"] = "primary";
            FileService.Result result = new FileService.Result();
            result.Metadata = metadata;
            result.Payload = png;
            return result;
        }

        private static byte[] Encode(int width, int height)
        {
            try
            {
                using (Bitmap bitmap = new Bitmap(width, height, PixelFormat.Format24bppRgb))
                {
                    using (Graphics graphics = Graphics.FromImage(bitmap))
                    {
                        graphics.CopyFromScreen(
                            0, 0, 0, 0, new Size(width, height),
                            CopyPixelOperation.SourceCopy);
                    }
                    using (MemoryStream buffer = new MemoryStream())
                    {
                        bitmap.Save(buffer, ImageFormat.Png);
                        if (buffer.Length > ScreenLimits.MaxPngBytes)
                        {
                            throw new ProtocolError(
                                "screen_too_large",
                                "the encoded PNG exceeds the " +
                                Number(ScreenLimits.MaxPngBytes) + " byte ceiling",
                                false);
                        }
                        return buffer.ToArray();
                    }
                }
            }
            catch (ProtocolError)
            {
                throw;
            }
            catch (Exception error)
            {
                throw Failure("the desktop capture failed: " + Describe(error), 0);
            }
        }

        private static string Describe(Exception error)
        {
            ExternalException external = error as ExternalException;
            if (external != null && external.ErrorCode != 0)
            {
                return error.Message + " (HRESULT 0x" +
                    external.ErrorCode.ToString("x8", CultureInfo.InvariantCulture) + ")";
            }
            return error.Message;
        }

        private static ProtocolError Failure(string message, int win32Error)
        {
            string detail = win32Error > 0
                ? message + " (Win32 error " + Number(win32Error) + ")"
                : message;
            return new ProtocolError("screen_capture_failed", detail, true);
        }

        private static string Number(int value)
        {
            return value.ToString(CultureInfo.InvariantCulture);
        }
    }
}
