# Modele

| Plik | Model | Źródło | Licencja |
|---|---|---|---|
| `face_detection_yunet_2023mar.onnx` | YuNet — wykrywanie twarzy i 5 punktów charakterystycznych | [OpenCV Zoo](https://github.com/opencv/opencv_zoo/tree/main/models/face_detection_yunet) | MIT — `LICENSE-YuNet-MIT.txt` |
| `face_recognition_sface_2021dec.onnx` | SFace — wektor 128 cech twarzy | [OpenCV Zoo](https://github.com/opencv/opencv_zoo/tree/main/models/face_recognition_sface) | Apache 2.0 — `LICENSE-SFace-Apache-2.0.txt`; dane treningowe nieudokumentowane, zob. niżej |

Pliki są niezmienione względem OpenCV Zoo (gałąź `main`, pobrane 2026-10-29).

**SFace — dane treningowe.** OpenCV Zoo nie podaje, na jakich danych wytrenowano ten plik. Oryginalna praca o SFace
używa zbiorów CASIA-WebFace, VGGFace2 i MS1MV2, które dopuszczają tylko użycie badawcze. Pytanie o pochodzenie danych
i użycie komercyjne jest otwarte: [opencv_zoo #313](https://github.com/opencv/opencv_zoo/issues/313) (od lipca 2026,
bez odpowiedzi opiekunów). Przy użyciu komercyjnym albo instytucjonalnym to ryzyko trzeba ocenić samodzielnie.
