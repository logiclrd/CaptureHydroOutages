using CefSharp;
using CefSharp.OffScreen;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace CaptureHydroOutages
{
	class Program
	{
		const string ManitobaHydroOutagesURL = "https://account.hydro.mb.ca/Portal/outeroutage.aspx";

		static void DoLog(Action<TextWriter> action)
		{
			for (int i = 0; i < 5; i++)
			{
				try
				{
					using (var log = new StreamWriter("CaptureHydroOutages.log", append: true) { AutoFlush = true })
						action(log);

					break;
				}
				catch { }

				Thread.Sleep(100);
			}
		}

		static void Log(string text)
		{
			DoLog(writer => writer.WriteLine($"[{Process.GetCurrentProcess().Id}] {text}"));
		}

		static void Log(string format, params object[] args)
		{
			Log(string.Format(format, args));
		}

		static async Task Main(string[] args)
		{
			Log("=================");
			Log("Starting at: {0}", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fffffff"));

			Bitmap screenshotBitmap = null;

			var browserSettings = new BrowserSettings();

			browserSettings.WindowlessFrameRate = 5	;

			var requestContextSettings = new RequestContextSettings();

			requestContextSettings.CachePath = Path.GetFullPath("cache");

			using (var requestContext = new RequestContext(requestContextSettings))
			using (var browser = new ChromiumWebBrowser(ManitobaHydroOutagesURL, browserSettings, requestContext))
			{
				var blocker = new SemaphoreSlim(0);

				var token = Guid.NewGuid().ToString();

				// SignalScript doesn't seem to be working with the new embedding, which uses an IFRAME.
				/*
				string SignalScript = new StreamReader(typeof(Program).Assembly.GetManifestResourceStream("CaptureHydroOutages.SignalScript.js")).ReadToEnd().Replace("TOKEN", token);

				browser.ConsoleMessage +=
					(sender, e) =>
					{
						Debug.WriteLine(e.Message);

						int tokenOffset = e.Message.IndexOf(token);

						if (tokenOffset >= 0)
						{
							Log("SIGNAL FROM SCRIPT: " + e.Message);

							string message = e.Message.Substring(tokenOffset + token.Length).Split(new[] { '|' }, StringSplitOptions.RemoveEmptyEntries).First();

							if (message == "notready")
								Task.Delay(100).ContinueWith((task) => browser.ExecuteScriptAsyncWhenPageLoaded(SignalScript));
							else
							{
								Log("UNBLOCKING");

								blocker.Release();

								if (message == "error")
								{
									string error = e.Message.Split(new[] { token }, StringSplitOptions.None)[1].Split('|').Last();
									Log("ERROR FROM SCRIPT: " + error);
								}
							}
						}
					};

				browser.FrameLoadStart +=
					(sender, e) =>
					{
						e.Frame.ExecuteJavaScriptAsync(SignalScript);
					};
				*/

				browser.Size = new Size(1920, 2000);

				TimeSpan GracePeriodDuration = TimeSpan.FromSeconds(5);

				var gracePeriodEnd = DateTime.UtcNow;

				var waiter = new ManualResetEvent(initialState: false);

				object sync = new object();

				async void TakeScreenshot()
				{
					bool requireFreshRender = false;

					Log("TakeScreenshot(): Registering for notification of the next render");

					while (true)
					{
						var bitmap = await browser.ScreenshotAsync(ignoreExistingScreenshot: requireFreshRender, PopupBlending.Main);

						requireFreshRender = true;

						if (bitmap == null)
						{
							Log("TakeScreenshot(): Did not receive a bitmap from the render process");
							break;
						}
						else
						{
							Log("TakeScreenshot(): Received bitmap from render process");

							lock (sync)
							{
								screenshotBitmap?.Dispose();
								screenshotBitmap = (Bitmap)bitmap.Clone();
							}

							gracePeriodEnd = DateTime.UtcNow + GracePeriodDuration;

							waiter.Set();
						}
					}
				}

				/*
				DateTime refreshViewAfter = DateTime.UtcNow.AddSeconds(20);
				int refreshCount = 0;

				while (true)
				{
					var remaining = refreshViewAfter - DateTime.UtcNow;

					if (remaining < TimeSpan.Zero)
					{
						if (refreshCount >= 5)
						{
							Log("ERROR: Tried refreshing {0} times, failed to receive semaphore signal from script, aborting, no image captured at {1}", refreshCount, DateTime.Now);
							Environment.Exit(1);
						}

						refreshCount++;

						Log("WARNING: Gave up on receiving the initialization token at {0}, beginning reload attempt {1}", DateTime.Now, refreshCount);

						browser.Reload();
						browser.ExecuteScriptAsyncWhenPageLoaded(SignalScript);

						refreshViewAfter = DateTime.UtcNow.AddSeconds(20);

						continue;
					}

					var acquired = await blocker.WaitAsync(remaining);

					if (acquired)
						break;
				}

				Log("TOKEN RECEIVED, BEGINNING SCREENSHOTS, WILL WAIT UNTIL 5 SECONDS ELAPSE WITH NO SCREENSHOT");
				*/
				Log("Waiting 20 seconds");

				Thread.Sleep(TimeSpan.FromSeconds(20));

				Log("Beginning screenshots");

				TakeScreenshot();

				await waiter.AsTask();

				Log("WOKEN UP");

				Bitmap capturedScreenshot = null;

				while (true)
				{
					var remaining = gracePeriodEnd - DateTime.UtcNow;

					if (remaining <= TimeSpan.Zero)
						break;

					Log("WAITING {0}", remaining);

					await Task.Delay(remaining);

					lock (sync)
					{
						capturedScreenshot = screenshotBitmap;
						screenshotBitmap = null;
					}
				}

				string fileName = "screenshot-captured-" + DateTime.Now.ToString("yyyyMMdd_HHmm");

				if (capturedScreenshot == null)
					Log("ERROR: Did not manage to capture a screenshot");
				else
				{
					/*
					await browser
						.EvaluateScriptAsync("document.getElementsByClassName('last_updated')[0].innerText")
						.ContinueWith(
							(task) =>
							{
								var response = task.Result;

								if (response.Success && (response.Result != null))
								{
									var formattedDateTimeString = response.Result.ToString();

									formattedDateTimeString = formattedDateTimeString.Replace("a.m.", "AM");
									formattedDateTimeString = formattedDateTimeString.Replace("p.m.", "PM");

									try
									{
										var lastUpdatedDateTime = DateTime.ParseExact(formattedDateTimeString, format: "dddd, MMMM d 'at' h:mm tt", CultureInfo.InvariantCulture);

										fileName += "-updated-" + lastUpdatedDateTime.ToString("yyyyMMdd_HHmm");
									}
									catch (Exception e)
									{
										Log("ERROR: Did not manage to determine when the data set was last updated, formatted date/time string: " + formattedDateTimeString);
										Log("=> " + e);
									}
								}
								else
									Log("ERROR: Did not manage to determine when the data set was last updated");
							});
					*/

					await browser
						.EvaluateScriptAsync(@"
var rect = document.getElementById('outage_map_canvas').getBoundingClientRect();

rect.left += window.scrollX;
rect.top += window.scrollY;

`${rect.top},${rect.right},${rect.bottom},${rect.left}`;")
						.ContinueWith(
							(task) =>
							{
								var response = task.Result;

								if (response.Success && (response.Result != null))
								{
									string rectString = response.Result.ToString();

									Log("Received bounding rectangle: {0}", rectString);

									string[] parts = rectString.Split(',');

									int top = (int)Math.Floor(double.Parse(parts[0]));
									int right = (int)Math.Ceiling(double.Parse(parts[1]));
									int bottom = (int)Math.Ceiling(double.Parse(parts[2]));
									int left = (int)Math.Floor(double.Parse(parts[3]));

									if ((top < bottom) && (left < right)
									 && (top >= 0) && (left >= 0)
									 && (bottom <= capturedScreenshot.Height) && (right <= capturedScreenshot.Width))
									{
										Log("=> Cropping screenshot");

										int width = right - left;
										int height = bottom - top;

										var cropped = new Bitmap(width, height, capturedScreenshot.PixelFormat);

										using (var g = Graphics.FromImage(cropped))
										{
											g.DrawImage(
												capturedScreenshot,
												destRect: new Rectangle(0, 0, width, height),
												srcRect: new Rectangle(left, top, width, height),
												srcUnit: GraphicsUnit.Pixel);
										}

										capturedScreenshot.Dispose();
										capturedScreenshot = cropped;
									}
								}
							});
				}

				Log("SAVING: " + fileName);

				capturedScreenshot.Save(fileName + ".png");
			}
		}
	}
}
