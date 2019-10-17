using CefSharp;
using CefSharp.OffScreen;
using System;
using System.Collections.Generic;
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
		const string ManitobaHydroOutagesURL = "https://www.hydro.mb.ca/outages/";

		static async Task Main(string[] args)
		{
			var log = new StreamWriter("CaptureHydroOutages.log", append: true) { AutoFlush = true };

			log.WriteLine("=================");
			log.WriteLine("Starting at: {0}", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fffffff"));

			Bitmap screenshotBitmap = null;

			var browserSettings = new BrowserSettings();

			browserSettings.WindowlessFrameRate = 5	;

			var requestContextSettings = new RequestContextSettings();

			requestContextSettings.CachePath = "cache";

			using (var requestContext = new RequestContext(requestContextSettings))
			using (var browser = new ChromiumWebBrowser(ManitobaHydroOutagesURL, browserSettings, requestContext))
			{
				var blocker = new SemaphoreSlim(0);

				var token = Guid.NewGuid().ToString();

				string SignalScript = new StreamReader(typeof(Program).Assembly.GetManifestResourceStream("CaptureHydroOutages.SignalScript.js")).ReadToEnd().Replace("TOKEN", token);

				browser.ConsoleMessage +=
					(sender, e) =>
					{
						int tokenOffset = e.Message.IndexOf(token);

						if (tokenOffset >= 0)
						{
							log.WriteLine("SIGNAL FROM SCRIPT: " + e.Message);

							string message = e.Message.Substring(tokenOffset + token.Length).Split(new[] { '|' }, StringSplitOptions.RemoveEmptyEntries).First();

							if (message == "notready")
								Task.Delay(100).ContinueWith((task) => browser.ExecuteScriptAsyncWhenPageLoaded(SignalScript));
							else
							{
								log.WriteLine("UNBLOCKING");

								blocker.Release();

								if (message == "error")
								{
									string error = e.Message.Split(new[] { token }, StringSplitOptions.None)[1].Split('|').Last();
									log.WriteLine("ERROR FROM SCRIPT: " + error);
								}
							}
						}
					};

				browser.ExecuteScriptAsyncWhenPageLoaded(SignalScript);

				browser.Size = new Size(1920, 2000);

				TimeSpan GracePeriodDuration = TimeSpan.FromSeconds(5);

				var gracePeriodEnd = DateTime.UtcNow;

				var waiter = new ManualResetEvent(initialState: false);

				async void TakeScreenshot()
				{
					log.WriteLine("TakeScreenshot(): Registering for notification of the next render");

					var bitmap = await browser.ScreenshotAsync(ignoreExistingScreenshot: true, PopupBlending.Main);

					if (bitmap == null)
						log.WriteLine("TakeScreenshot(): Did not receive a bitmap from the render process");
					else
					{
						log.WriteLine("TakeScreenshot(): Received bitmap from render process");

						screenshotBitmap?.Dispose();
						screenshotBitmap = (Bitmap)bitmap.Clone();

						gracePeriodEnd = DateTime.UtcNow + GracePeriodDuration;

						waiter.Set();
					}

					TakeScreenshot();
				}

				DateTime refreshViewAfter = DateTime.UtcNow.AddSeconds(20);
				int refreshCount = 0;

				while (true)
				{
					var remaining = refreshViewAfter - DateTime.UtcNow;

					if (remaining < TimeSpan.Zero)
					{
						if (refreshCount >= 5)
						{
							log.WriteLine("ERROR: Tried refreshing {0} times, failed to receive semaphore signal from script, aborting, no image captured at {1}", refreshCount, DateTime.Now);
							Environment.Exit(1);
						}

						refreshCount++;

						log.WriteLine("WARNING: Gave up on receiving the initialization token at {0}, beginning reload attempt {1}", DateTime.Now, refreshCount);

						browser.Reload();
						browser.ExecuteScriptAsyncWhenPageLoaded(SignalScript);

						refreshViewAfter = DateTime.UtcNow.AddSeconds(20);

						continue;
					}

					var acquired = await blocker.WaitAsync(remaining);

					if (acquired)
						break;
				}

				log.WriteLine("TOKEN RECEIVED, BEGINNING SCREENSHOTS, WILL WAIT UNTIL 5 SECONDS ELAPSE WITH NO SCREENSHOT");

				TakeScreenshot();

				await waiter.AsTask();

				log.WriteLine("WOKEN UP");

				while (true)
				{
					var remaining = gracePeriodEnd - DateTime.UtcNow;

					if (remaining <= TimeSpan.Zero)
						break;

					log.WriteLine("WAITING {0}", remaining);

					await Task.Delay(remaining);
				}

				string fileName = "screenshot-captured-" + DateTime.Now.ToString("yyyyMMdd_HHmm");

				if (screenshotBitmap == null)
					log.WriteLine("ERROR: Did not manage to capture a screenshot");
				else
				{
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

									var lastUpdatedDateTime = DateTime.ParseExact(formattedDateTimeString, format: "dddd, MMMM d 'at' h:mm tt", CultureInfo.InvariantCulture);

									fileName += "-updated-" + lastUpdatedDateTime.ToString("yyyyMMdd_HHmm");
								}
								else
									log.WriteLine("ERROR: Did not manage to determine when the data set was last updated");
							});

					await browser
						.EvaluateScriptAsync(@"
var rect = document.getElementById('outagemap').getBoundingClientRect();

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

									log.WriteLine("Received bounding rectangle: {0}", rectString);

									string[] parts = rectString.Split(',');

									int top = (int)Math.Floor(double.Parse(parts[0]));
									int right = (int)Math.Ceiling(double.Parse(parts[1]));
									int bottom = (int)Math.Ceiling(double.Parse(parts[2]));
									int left = (int)Math.Floor(double.Parse(parts[3]));

									if ((top < bottom) && (left < right)
									 && (top >= 0) && (left >= 0)
									 && (bottom <= screenshotBitmap.Height) && (right <= screenshotBitmap.Width))
									{
										log.WriteLine("=> Cropping screenshot");

										int width = right - left;
										int height = bottom - top;

										var cropped = new Bitmap(width, height, screenshotBitmap.PixelFormat);

										using (var g = Graphics.FromImage(cropped))
										{
											g.DrawImage(
												screenshotBitmap,
												destRect: new Rectangle(0, 0, width, height),
												srcRect: new Rectangle(left, top, width, height),
												srcUnit: GraphicsUnit.Pixel);
										}

										screenshotBitmap.Dispose();
										screenshotBitmap = cropped;
									}
								}
							});
				}

				log.WriteLine("SAVING: " + fileName);

				screenshotBitmap.Save(fileName + ".png");
			}
		}
	}
}
