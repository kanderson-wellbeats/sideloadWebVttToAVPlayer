public class CustomResourceLoaderDelegate : AVAssetResourceLoaderDelegate
{
	public const string LoaderInterceptionWorkaroundUrlPrefix = "CUSTOMSCHEME"; // a scheme other than http(s) needs to be used for AVUrlAsset's URL or ShouldWaitForLoadingOfRequestedResource will never be called
	private const string SubtitlePlaylistBoomerangUrlPrefix = LoaderInterceptionWorkaroundUrlPrefix + "SubtitlePlaylist";
	private const string SubtitleBoomerangUrlSuffix = "m3u8";
	private readonly NSUrlSession _session;
	private readonly List<SubtitleBundle> _subtitleBundles;

	public CustomResourceLoaderDelegate(IEnumerable<WorkoutSubtitleDto> subtitles)
	{
		_subtitleBundles = subtitles.Select(subtitle => new SubtitleBundle {SubtitleDto = subtitle}).ToList();
		_session = NSUrlSession.FromConfiguration(NSUrlSessionConfiguration.DefaultSessionConfiguration);
	}

	public override bool ShouldWaitForLoadingOfRequestedResource(AVAssetResourceLoader resourceLoader,
		AVAssetResourceLoadingRequest loadingRequest)
	{
		var requestString = loadingRequest.Request.Url.AbsoluteString;
		var dataRequest = loadingRequest.DataRequest;

		if (requestString.StartsWith(SubtitlePlaylistBoomerangUrlPrefix))
		{
			var uri = new Uri(requestString);
			var targetLanguage = uri.Host.Split(".").First();
			var targetSubtitle = _subtitleBundles.FirstOrDefault(s => s.SubtitleDto.Language == targetLanguage);

			Debug.WriteLine("### SUBTITLE PLAYLIST " + requestString);
			if (targetSubtitle == null)
			{
				loadingRequest.FinishLoadingWithError(new NSError());
				return true;
			}
			var subtitlePlaylistTask = _session.CreateDataTask(NSUrlRequest.FromUrl(NSUrl.FromString(targetSubtitle.SubtitleDto.CloudFileURL)),
				(data, response, error) =>
				{
					if (error != null)
					{
						loadingRequest.FinishLoadingWithError(error);
						return;
					}
					if (data == null || !data.Any())
					{
						loadingRequest.FinishLoadingWithError(new NSError());
						return;
					}
					MakePlaylistAndFragments(targetSubtitle, Encoding.UTF8.GetString(data.ToArray()));

					loadingRequest.DataRequest.Respond(NSData.FromString(targetSubtitle.Playlist));
					loadingRequest.FinishLoading();
				});
			subtitlePlaylistTask.Resume();
			return true;
		}

		if (!requestString.ToLower().EndsWith(".ism/manifest(format=m3u8-aapl)") || // lots of fragment requests will come through, we're just going to fix their URL so they can proceed normally (getting bits of video and audio)
			(dataRequest != null && 
			 dataRequest.RequestedOffset == 0 && // this catches the first (of 3) master playlist requests. the thing sending out these requests and handling the responses seems unable to be satisfied by our handling of this (just for the first request), so that first request is just let through. if you mess with request 1 the whole thing stops after sending request 2. although this means the first request doesn't get the same edited master playlist as the second or third, apparently that's fine.
			 dataRequest.RequestedLength == 2 &&
			 dataRequest.CurrentOffset == 0))
		{
			Debug.WriteLine("### REDIRECTING REQUEST " + requestString);
			var redirect = new NSUrlRequest(new NSUrl(requestString.Replace(LoaderInterceptionWorkaroundUrlPrefix, "")));
			loadingRequest.Redirect = redirect;
			var fakeResponse = new NSHttpUrlResponse(redirect.Url, 302, null, null);
			loadingRequest.Response = fakeResponse;
			loadingRequest.FinishLoading();
			return true;
		}

		var correctedRequest = new NSMutableUrlRequest(new NSUrl(requestString.Replace(LoaderInterceptionWorkaroundUrlPrefix, "")));
		if (dataRequest != null)
		{
			var headers = new NSMutableDictionary();
			foreach (var requestHeader in loadingRequest.Request.Headers)
			{
				headers.Add(requestHeader.Key, requestHeader.Value);
			}
			correctedRequest.Headers = headers;
		}

		var masterPlaylistTask = _session.CreateDataTask(correctedRequest, (data, response, error) =>
		{
			Debug.WriteLine("### REQUEST CARRIED OUT AND RESPONSE EDITED " + requestString);
			if (error == null)
			{
				var dataString = Encoding.UTF8.GetString(data.ToArray());
				var stringWithSubsAdded = AddSubs(dataString);

				dataRequest?.Respond(NSData.FromString(stringWithSubsAdded));

				loadingRequest.FinishLoading();
			}
			else
			{
				loadingRequest.FinishLoadingWithError(error);
			}
		});
		masterPlaylistTask.Resume();
		return true;
	}

	private string AddSubs(string dataString)
	{
		var tracks = dataString.Split("\r\n").ToList();
		for (var ii = 0; ii < tracks.Count; ii++)
		{
			if (tracks[ii].StartsWith("#EXT-X-STREAM-INF"))
			{
				tracks[ii] += ",SUBTITLES=\"subs\"";
			}
		}

		tracks.AddRange(_subtitleBundles.Select(subtitle => "#EXT-X-MEDIA:TYPE=SUBTITLES,GROUP-ID=\"subs\",LANGUAGE=\"" + subtitle.SubtitleDto.Language + "\",NAME=\"" + subtitle.SubtitleDto.Title + "\",AUTOSELECT=YES,URI=\"" + SubtitlePlaylistBoomerangUrlPrefix + "://" + subtitle.SubtitleDto.Language + "." + SubtitleBoomerangUrlSuffix + "\""));

		var finalPlaylist = string.Join("\r\n", tracks);
		return finalPlaylist;
	}

	private void MakePlaylistAndFragments(SubtitleBundle subtitle, string vtt)
	{
		var noWhitespaceVtt = vtt.Replace(" ", "").Replace("\n", "").Replace("\r", "");
		var arrowIndex = noWhitespaceVtt.LastIndexOf("-->");
		var afterArrow = noWhitespaceVtt.Substring(arrowIndex);
		var firstColon = afterArrow.IndexOf(":");
		var period = afterArrow.IndexOf(".");
		var timeString = afterArrow.Substring(firstColon - 2, period /*(+ 2 - 2)*/);
		var lastTime = TimeSpan.Parse(timeString);

		var resultLines = new List<string>
		{
			"#EXTM3U",
			"#EXT-X-TARGETDURATION:" + lastTime.TotalSeconds,
			"#EXT-X-VERSION:3",
			"#EXT-X-MEDIA-SEQUENCE:0",
			"#EXT-X-PLAYLIST-TYPE:VOD",
			"#EXTINF:" + lastTime.TotalSeconds,
			subtitle.SubtitleDto.CloudFileURL,
			"#EXT-X-ENDLIST"
		};

		subtitle.Playlist = string.Join("\r\n", resultLines);
	}

	private class SubtitleBundle
	{
		public WorkoutSubtitleDto SubtitleDto { get; set; }
		public string Playlist { get; set; }
	}

	public class WorkoutSubtitleDto
	{
		public int WorkoutID { get; set; }
		public string Language { get; set; }
		public string Title { get; set; }
		public string CloudFileURL { get; set; }
	}
}
