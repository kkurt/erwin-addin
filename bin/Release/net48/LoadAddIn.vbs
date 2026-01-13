' EliteSoft Erwin Add-In Auto Loader
On Error Resume Next

' Wait a moment for erwin to fully initialize
WScript.Sleep 3000

' Create the add-in object and run it
Set addIn = CreateObject("EliteSoft.Erwin.AddIn")
If Not addIn Is Nothing Then
    addIn.Execute
End If

Set addIn = Nothing
