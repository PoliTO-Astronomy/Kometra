# Kometra

![Platform](https://img.shields.io/badge/Platform-Windows%20%7C%20macOS%20%7C%20Linux-lightgrey)
![Framework](https://img.shields.io/badge/Framework-.NET%208.0-512BD4)
![UI](https://img.shields.io/badge/UI-Avalonia-purple)
![License](https://img.shields.io/badge/License-GPLv3-blue)

**Kometra** is an integrated, cross-platform, and high-performance software system specifically designed for the astrometric processing and advanced morphological analysis of cometary images. 

🌍 *The application interface is fully available in both **English** and **Italian**.*

The project originated as a Master's Thesis in Computer Engineering at Politecnico di Torino, in close collaboration with amateur astronomers affiliated with the Asiago Astrophysical Observatory, with the aim of providing the scientific and amateur astronomy community with a modern tool to overcome the limitations of traditional workflows.

---

## Main Features

Kometra offers a comprehensive pipeline for astronomical data processing, from importing raw files to exporting morphological analysis:

- **Proprietary FITS Data Infrastructure:** Custom-built FITS parser supporting Multi-Extension FITS (MEF) and Tiled Image Compression (Rice and bit-exact GZIP algorithms).
- **Astrometry and Dynamic Tracking:** Sub-pixel detection of the cometary optocenter for dynamic alignment. Integration with local astrometric solvers (ASTAP) and ephemeris databases (JPL/NASA).
- **FITS Header Editor & Diagnostics:** Interactive interface for non-destructive metadata inspection, featuring a traffic-light evaluation system for astrometric requirements (*Health Evaluator*).
- **Starless Pipeline (Segmentation and Inpainting):** Automatic removal of field stars using mathematical morphology algorithms and stochastic background sky reconstruction, preserving the photometric integrity of the coma.
- **Advanced Morphological Filters:** Rigorous implementation of rotational, radial, and tensorial filters (e.g., Larson-Sekanina, Median Coma Model, Frangi Hessian) for the extraction of dust jets and low-contrast structures.
- **Node Tree Workflow:** Non-destructive paradigm that allows conducting parallel experiments (A/B testing) and real-time previews without altering the original files.
- **Media & Scientific Export:** Export of scientific data in FITS/MEF (compressed or uncompressed) and automated generation of video timelapses for outreach.

## Tech Stack

The software has been engineered to maximize performance and ensure portability:

- **Core & Logic:** C# / .NET
- **Graphical User Interface (GUI):** Avalonia UI (MVVM Pattern)
- **Mathematical Engine:** OpenCV (via .NET wrapper) for high-precision matrix manipulations.
- **Memory Management:** Optimizations for massive floating-point matrices through concurrent multithreading, `ArrayPool`, and `ThreadLocal` to prevent Garbage Collector bottlenecks.

## Installation and Build

Kometra is designed to be completely standalone. Users do not need to install any frameworks or native libraries.

### Option 1: Pre-compiled Releases (Recommended)
You can download the ready-to-use binaries for Windows, macOS, and Linux from the [Releases page](https://github.com/PoliTO-Astronomy/Kometra/releases).
Thanks to .NET's self-contained deployment, **you do not need to install the .NET Runtime or OpenCV**. Just download, extract, and run the executable.

### Option 2: Build from source
If you prefer to compile the application yourself, explicit and reproducible automated build instructions are provided via GitHub Actions in the `.github/workflows/` directory.

**Prerequisites for building:**
- [.NET 8.0 SDK](https://dotnet.microsoft.com/download) (or higher)

**Build instructions:**
Clone the repository and build the project using the .NET CLI. The build engine will automatically fetch all required managed and native dependencies:
```bash
git clone [https://github.com/PoliTO-Astronomy/Kometra.git](https://github.com/PoliTO-Astronomy/Kometra.git)
cd kometra
dotnet build -c Release
```

## Screenshots

### Workspace & Node Tree
<img src="https://github.com/user-attachments/assets/da343126-9398-4f1e-b01f-7ebc48344bd2" width="65%" alt="Workspace & Node Tree">

<br>

### FITS Header Editor
<img src="https://github.com/user-attachments/assets/a459fe52-a5f8-4967-ba74-907c82ac6c33" width="65%" alt="FITS Header Editor">

<br>

### Morphological Filters
<img src="https://github.com/user-attachments/assets/5c7e49e6-5bc7-4402-9b74-57f4fa18b6e9" width="65%" alt="Morphological Filters">

<br>

### Export Tool
<img src="https://github.com/user-attachments/assets/359ce463-5318-47f0-ae21-cfcadf8d36f8" width="65%" alt="Export">

---

## License

This project is licensed under the **GNU General Public License v3.0** (GPLv3) - see the [LICENSE](LICENSE) file for details.
