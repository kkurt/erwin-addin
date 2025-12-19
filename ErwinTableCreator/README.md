# erwin Table Creator Addon

erwin Data Modeler icin basit bir tablo olusturma addon'u (C#).

## Ozellikler

- Aktif erwin modeline yeni tablo (Entity) ekleme
- SCAPI (Script Client API) kullanarak erwin ile iletisim
- Basit ve kullanimi kolay arayuz

## Gereksinimler

- erwin Data Modeler (r9 veya uzeri) kurulu ve lisansli olmali
- .NET Framework 4.8
- Windows x64
- Visual Studio 2019 veya uzeri

## Kurulum

1. `ErwinTableCreator.csproj` dosyasini Visual Studio ile acin
2. Build > Build Solution ile derleyin (Ctrl+Shift+B)
3. **Onemli**: erwin Data Modeler'in acik oldugunden emin olun
4. Uygulamayi calistirin (F5)

## Kullanim

1. erwin Data Modeler'da bir model acin
2. Bu addon'u calistirin
3. Tablo adini girin
4. "Create Table" butonuna basin

## SCAPI Hakkinda

Bu addon, erwin SCAPI (Script Client API) kullanarak erwin Data Modeler ile iletisim kurar:

```csharp
// SCAPI baglantisi
Type scapiType = Type.GetTypeFromProgID("erwin9.SCAPI");
dynamic oApplication = Activator.CreateInstance(scapiType);

// Model erisimi
dynamic persistenceUnits = oApplication.PersistenceUnits;

// Entity olusturma
dynamic oNewEntity = oModelObjects.CreateNew("Entity", oRootObject, oPropertyBag);
```

## Kaynaklar

- [erwin Data Modeler API Reference](https://bookshelf.erwin.com/bookshelf/public_html/Content/PDFs/API%20Reference.pdf)
- [erwin API Wrapper (GitHub)](https://github.com/SSAgov/erwin-api-wrapper)

## Lisans

MIT
