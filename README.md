# sideloadWebVttToAVPlayer
This is just a code sample that describes how to extend AVAssetResourceLoaderDelegate in order to allow sideloading of WebVtt files for videos streamed to AVPlayer. I'm putting this up here because the full explanation is too long to post on Apple developer forums. 


I finally got this working. I'm dropping this explanation here to try to save some future people a lot of pain. Lots of stuff I found online was wrong, or left out confusing pieces, or had a bunch of extra irrelevant information, or a mixture of all three. So to begin I'll describe what I'm trying to do. My backend server is Azure Media Services, and it's been really great for streaming different resolution video as needed but it just doesn't really support WebVtt. Yeah you can host a file on there, but it seems it cannot give us a master playlist that includes a reference to the subtitles playlist (as Apple requires). It seems both Apple and Microsoft decided what they were going to do with subtitles back in like 2012 and haven't touched it since. At that time they either didn't talk to each other or deliberately went opposite directions, but they happen to have poor intercompatibilty, and now devs like us are forced to stretch the gap between the behemoths. All I'm wanting to do is add subtitles to on-demand videos played in AVPlayer being served by Azure Media Services when I have a WebVtt file - nothing more, nothing less. I'll start by describing everything in words, then I'll put the actual code at the end.

### Here is the extremely condensed version of what you need to do:
1) Intercept the requests for the master playlist and return an edited version of it that references the subtitle playlists (multiple for multiple languages, or just one for one language)
2) Select a subtitle to show (well documented on https://developer.apple.com/documentation/avfoundation/media_playback_and_selection/selecting_subtitles_and_alternative_audio_tracks ) 
3) Intercept requests to the subtitle playlists that will come through (after you've selected a subtitle to show) and return playlists you've built on the fly that reference the WebVtt files on the server
That's it. Not too much, except there are many complications that get in the way that I had to discover myself. I'll describe them each first briefly and then in greater detail.

### Brief complication explanations:
1) Many requests will come be coming through, but you should only (and can only) handle a couple of them yourself, the others need to be allowed to pass through untouched. I will describe which ones need handling and which ones don't and how to handle them.
2) Apple decided a simple HTTP request was not good enough and decided to obscure things by translating it into a weird double-identity AVAssetResourceLoadingRequest thing that has a DataRequest property (AVAssetResourceLoadingDataRequest) and a ContentInformationRequest property (AVAssetResourceLoadingContentInformationRequest). I still don't understand why this was necessary or what benefit it brings, but what I've done here with them is working. Some promising blogs/resources seem to suggest you have to mess with the ContentInformationRequest but I find that you can simply ignore the ContentInformationRequest, and in fact messing with it more often than not just breaks things.
3) Apple suggests you segment your VTT file into small pieces, but you simply can't do this client-side (Apple disallows this), but luckily it also seems you don't actually have to do it, it's merely a suggestion.

### INTERCEPTING REQUESTS
To intercept requests, you have to subclass/extend AVAssetResourceLoaderDelegate and the method of interest is the ShouldWaitForLoadingOfRequestedResource method. To make use of the delegate, instantiate your AVPlayer by handing it an AVPlayerItem but hand the AVPlayerItem an AVUrlAsset which has a delegate property you assign the delegate to. All the requests will come through the ShouldWaitForLoadingOfRequestedResource method so that's where all the business will happen, except for one sneaky complication - the method will only be invoked if requests begin with something other than http/https, so my advice is to stick a constant string at the front of the Url you're using to create your AVUrlAsset, which you can then just shave off after the requests comes in to your delegate - let's call that "CUSTOMSCHEME". This part is described in a couple of places online, but it can be super frustrating if you don't know you have to do it because it will seem like nothing is happening at all.

### INTERCEPTING - TYPE A) redirecting
Ok so now we're intercepting requests, but you don't want to (/can't) handle them all yourself. Some of the requests you just want to allow to pass through. You do this by doing the following:
1) create a new NSUrlRequest to the CORRECTED Url (shave off that "CUSTOMSCHEME" part from earlier) and set it to the Redirect property on the LoadingRequest
2) create a new NSHttpUrlResponse with that same corrected Url and a 302 code and set it to the Response property on the LoadingRequest
3) call FinishLoading on the LoadingRequest
4) return true
With those steps you can add in breakpoints and stuff to debug and inspect all the requests that will come through, but they'll proceed normally so you won't break anything. However, this approach isn't just for debugging, it's also a necessary thing to do for several requests even in the finished project.

### INTERCEPTING - TYPE B) editing/faking response
When some requests come in, you'll want to do a request of your own so the response to your request (with some tweaking) can be used to fulfill the LoadingRequest. So do the following:
1) create an NSUrlSession and call the CreateDataTask method on the session (with a corrected URL - remove the "CUSTOMSCHEME") 
2) call Resume on the DataTask (outside of the callback on the DataTask)
3) return true
4) up in the DataTask's callback you'll have data, so (after doing your edits) you call Respond on the LoadingRequest's DataRequest property with that (edited) data, followed by calling FinishLoading on the LoadingRequest

### INTERCEPTING - which requests get which type of treatment
Lots of requests will come in, some need to be redirected, some need to be given manufactured/altered data responses. Here are the types of requests you'll see in the order they'll come in and what to do with each:
1) a request to the master playlist, but the DataRequest's RequestedLength is 2 - just redirect (TYPE A)
2) a request to the master playlist, but the DataRequest's RequestedLength matches the (unedited) length of the master playlist - do your own request to the master playlist so you can edit it and return the edited result (TYPE B)
3) a request to the master playist, but the DataRequest's RequestedLength is humongous - do the same thing as you did for the previous one (TYPE B)
4) lots of requests will come through for fragments of audio and video - all these requests need to be redirected (TYPE A)
5) once you get the master playlist edited correctly (and a subtitle selected) a request will come through for the subtitle playlist - edit this one to return a manufactured subtitle playlist (TYPE B)

### HOW TO EDIT THE PLAYLISTS - master playlist
The master playlist is easy to edit. The change is two things:
1) each video resource has its own line and they all need to be told about the subtitle group (for each line that starts with "#EXT-X-STREAM-INF" I'm adding ",SUBTITLES=\"subs\"" on the end)
2) new lines need to be added for each subtitle language/type, all belonging to the subtitle group with their own URL (so for each type, add a line like "#EXT-X-MEDIA:TYPE=SUBTITLES,GROUP-ID=\"subs\",LANGUAGE=\"!!!yourLanguageHere!!!",NAME=\"!!!yourNameHere!!!",AUTOSELECT=YES,URI=\"!!!yourCustomUrlHere!!!\""
The !!!yourCustomUrlHere!!! you use in step 2 will have to be detected by you when it's used for a request so you can return the manufactured subtitle playlist as part of the response, so set it to something unique. That Url will also have to use the "CUSTOMSCHEME" thing so that it comes to the delegate. You can also check out this streaming example to see how the manifest should look: https://developer.apple.com/streaming/examples/basic-stream-osx-ios5.html (sniff the network traffic with the browser debugger to see it).

### HOW TO EDIT THE PLAYLISTS - subtitle playlist
The subtitle playlist is a little more complicated. You have to make the whole thing yourself. The way I've done it is to actually grab the WebVtt file myself inside the DataTask callback, then parse the thing down to find the end of the very last timestamp sequence, convert that to an integer number of seconds, and then insert that value in a couple places in a big string. Again, you can use the example listed above and sniff network traffic to see a real example for yourself. So it looks like this:
	#EXTM3U
	#EXT-X-TARGETDURATION:!!!thatLengthIMentioned!!!
	#EXT-X-VERSION:3
	#EXT-X-MEDIA-SEQUENCE:0
	#EXT-X-PLAYLIST-TYPE:VOD
	#EXTINF:!!!thatLengthIMentioned!!!
	!!!absoluteUrlToTheWebVttFileOnTheServer!!!
	#EXT-X-ENDLIST
Note that the playlist does NOT segment the vtt file as Apple recommends because this can't be done client-side (source: https://developer.apple.com/forums/thread/113063?answerId=623328022#623328022 ). Also note that I do NOT put a comma at the end of the "EXTINF" line even though Apple's example here says to do that, because it seems to break it: https://developer.apple.com/videos/play/wwdc2012/512/
