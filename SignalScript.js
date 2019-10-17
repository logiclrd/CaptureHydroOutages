/*eslint no-inner-declarations: "off"*/
/*eslint no-extra-parens: "off"*/

try
{
	var ObserveDOM =
		(function ()
		{
			var MutationObserver = window.MutationObserver || window.WebKitMutationObserver;

			var implementation =
				function (obj, callback)
				{
					if (!obj || !obj.nodeType === 1) return; // validation

					var unhook = null;

					function CallUnhook()
					{
						if (unhook !== null)
							unhook();
					}

					function CallCallback(mutations)
					{
						mutations.some(
							(mutation) =>
							{
								callback(mutation, CallUnhook);
								return (unhook !== null);
							});
					}

					if (MutationObserver)
					{
						// define a new observer
						var obs = new MutationObserver(
							function (mutations, observer)
							{
								CallCallback(mutations);
							});

						// have the observer observe foo for changes in children
						unhook =
							function ()
							{
								obs.disconnect();
								unhook = null;
							};

						obs.observe(obj, { childList: true, subtree: true });
					}
					else
						throw "Missing MutationObserver feature";
				};

			return implementation;
		})();

	var outageMapNode = document.getElementById("outagemap");

	if (outageMapNode !== null)
	{
		console.log("OUTAGE MAP IS ALREADY IN THE DOM");
		ObserveOutageMap();
	}
	else
	{
		ObserveDOM(
			document.documentElement,
			function (documentMutation, unhook)
			{
				console.log("inspecting a document mutation");

				if (documentMutation.addedNodes && ("forEach" in documentMutation.addedNodes))
				{
					documentMutation.addedNodes.forEach(
						(node) =>
						{
							if (outageMapNode !== null)
								return;

							if ((node.tagName === "div") && (node.id === "outagemap"))
							{
								console.log("DETECTED ADDITION OF OUTAGE MAP ELEMENT");
								unhook();

								outageMapNode = node;

								ObserveOutageMap();
							}
						});
				}
			});
	}

	function ObserveOutageMap()
	{
		console.log("will observe: " + outageMapNode);

		ObserveDOM(
			outageMapNode,
			function (documentMutation, unhook)
			{
				documentMutation.addedNodes.forEach(
					(node) =>
					{
						if (node instanceof Text)
						{
							console.log("Detected Text being set: " + node.nodeValue);

							if (node.nodeValue.includes("Map data \u{A9}") && node.nodeValue.includes("Google"))
								console.log("This is the one! TOKEN|idle|");
						}
					});
			});
	}
}
catch (error)
{
	console.log('TOKEN|error|' + error + 'TOKEN');
}
