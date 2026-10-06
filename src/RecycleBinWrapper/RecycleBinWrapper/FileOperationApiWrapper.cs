using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.System.Com;
using Windows.Win32.UI.Shell;

namespace Utils;

[SupportedOSPlatform("windows6.0.6000")]
public static unsafe class FileOperationApiWrapper
{
    private static readonly Guid ClsidFileOperation = new("3ad05575-8857-4850-9277-11b85bdb8e09");

    private const int S_OK = 0;

    private struct RecycleItem
    {
        public string? OriginalPath;
        public string FullPath;
        public bool Queued;
    }

    /// <summary>
    /// Отправка одного файла/папки в корзину.
    /// </summary>
    /// <param name="filePath">Путь к удаляемому объекту.</param>
    /// <param name="throwOnError">Выбрасывать ли исключение при возникновении ошибки.</param>
    /// <returns><see langword="true"/>, если объект успешно удалён; иначе <see langword="false"/>.</returns>
    public static bool MoveToRecycleBin(string? filePath, bool throwOnError = true)
    {
        if (throwOnError)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        }
        else if (string.IsNullOrWhiteSpace(filePath))
        {
            return false;
        }

        return MoveToRecycleBin([filePath], throwOnError, out var failedPaths) && failedPaths.IsEmpty;
    }

    /// <summary>
    /// Отправка пакета файлов/папок в корзину с возвратом списка неудачных путей.
    /// </summary>
    /// <param name="filePaths">Коллекция путей к файлам/папкам.</param>
    /// <param name="throwOnError">Выбрасывать ли исключение при ошибке.</param>
    /// <param name="failedPaths">Пути, которые не удалось переместить в корзину.</param>
    /// <returns><see langword="true"/>, если все переданные объекты были успешно удалены; иначе <see langword="false"/>.</returns>
    public static bool MoveToRecycleBin(
        ReadOnlySpan<string?> filePaths,
        bool throwOnError,
        out ReadOnlySpan<string?> failedPaths)
    {
        if (filePaths.IsEmpty)
        {
            failedPaths = [];
            return true;
        }

        List<string?>? failedList = null;
        var validItems = new List<RecycleItem>(filePaths.Length);

        // 1. Предварительная валидация и нормализация путей
        for (var i = 0; i < filePaths.Length; i++)
        {
            var rawPath = filePaths[i];
            if (string.IsNullOrWhiteSpace(rawPath))
            {
                if (throwOnError)
                {
                    throw new ArgumentException(
                        $"Путь под индексом {i} пуст или содержит только пробелы.",
                        nameof(filePaths));
                }

                (failedList ??= []).Add(rawPath);
                continue;
            }

            string fullPath;
            try
            {
                fullPath = Path.GetFullPath(rawPath);
            }
            catch (Exception ex)
            {
                if (throwOnError)
                {
                    throw new ArgumentException($"Некорректный путь: '{rawPath}'.", nameof(filePaths), ex);
                }

                (failedList ??= []).Add(rawPath);
                continue;
            }

            if (!File.Exists(fullPath) && !Directory.Exists(fullPath))
            {
                if (throwOnError)
                {
                    throw new FileNotFoundException($"Файл или директория не найдены: '{fullPath}'.", fullPath);
                }

                (failedList ??= []).Add(rawPath);
                continue;
            }

            validItems.Add(new RecycleItem
            {
                OriginalPath = rawPath,
                FullPath = fullPath,
                Queued = false
            });
        }

        // Если не осталось путей для удаления через IFileOperation
        if (validItems.Count == 0)
        {
            failedPaths = failedList is { Count: > 0 } ? failedList.ToArray() : [];
            return failedPaths.IsEmpty;
        }

        // 2. Диспетчеризация выполнения в STA
        if (Thread.CurrentThread.GetApartmentState() == ApartmentState.STA)
        {
            try
            {
                ExecuteInternal(validItems, throwOnError, ref failedList);
            }
            catch when (!throwOnError)
            {
                // Подавляем любые исключения верхнего уровня при throwOnError = false
            }
        }
        else
        {
            Exception? dispatchException = null;
            var thread = new Thread(() =>
            {
                try
                {
                    ExecuteInternal(validItems, throwOnError, ref failedList);
                }
                catch (Exception ex)
                {
                    dispatchException = ex;
                }
            })
            {
                IsBackground = true
            };

            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            thread.Join();

            if (dispatchException != null)
            {
                if (throwOnError)
                {
                    throw new Win32Exception(
                        "Ошибка при отправке файлов в корзину через STA-поток.",
                        dispatchException);
                }

                // При throwOnError = false отмечаем все неудалённые элементы
                for (var i = 0; i < validItems.Count; i++)
                {
                    var item = validItems[i];
                    if (File.Exists(item.FullPath) || Directory.Exists(item.FullPath))
                    {
                        (failedList ??= []).Add(item.OriginalPath);
                    }
                }
            }
        }

        failedPaths = failedList is { Count: > 0 } ? failedList.ToArray() : [];
        return failedPaths.IsEmpty;
    }

    /// <summary>
    /// Тихая отправка файлов/папок в корзину (выбрасывает исключение при ошибке).
    /// </summary>
    public static void MoveToRecycleBin(ReadOnlySpan<string?> filePaths)
    {
        MoveToRecycleBin(filePaths, throwOnError: true, out _);
    }

    private static void ExecuteInternal(
        List<RecycleItem> items,
        bool throwOnError,
        ref List<string?>? failedList)
    {
        var coInitHr = PInvoke.CoInitializeEx(COINIT.COINIT_APARTMENTTHREADED);
        if (throwOnError)
        {
            coInitHr.ThrowOnFailure();
        }
        else if (coInitHr.Failed)
        {
            for (var i = 0; i < items.Count; i++)
            {
                (failedList ??= []).Add(items[i].OriginalPath);
            }

            return;
        }

        var needUninitialize = coInitHr.Value == S_OK;

        try
        {
            var clsid = ClsidFileOperation;

            IFileOperation? fileOp = null;

            try
            {
                var createInstanceHr = PInvoke.CoCreateInstance(
                    clsid,
                    null,
                    CLSCTX.CLSCTX_INPROC_SERVER,
                    out IFileOperation createdFileOp);

                fileOp = createdFileOp;

                if (throwOnError)
                {
                    createInstanceHr.ThrowOnFailure();
                }
                else if (createInstanceHr.Failed)
                {
                    for (var i = 0; i < items.Count; i++)
                    {
                        (failedList ??= []).Add(items[i].OriginalPath);
                    }

                    return;
                }

                const FILEOPERATION_FLAGS flags = FILEOPERATION_FLAGS.FOF_SILENT |
                                                  FILEOPERATION_FLAGS.FOF_NOCONFIRMATION |
                                                  FILEOPERATION_FLAGS.FOF_NOERRORUI |
                                                  FILEOPERATION_FLAGS.FOF_NOCONFIRMMKDIR |
                                                  FILEOPERATION_FLAGS.FOFX_RECYCLEONDELETE;

                createdFileOp.SetOperationFlags(flags);

                var queuedCount = 0;

                for (var i = 0; i < items.Count; i++)
                {
                    var item = items[i];
                    IShellItem? shellItem = null;

                    try
                    {
                        var hrItem = PInvoke.SHCreateItemFromParsingName(
                            item.FullPath,
                            null,
                            out shellItem);

                        if (throwOnError)
                        {
                            hrItem.ThrowOnFailure();
                        }
                        else if (hrItem.Failed)
                        {
                            (failedList ??= []).Add(item.OriginalPath);
                            continue;
                        }

                        createdFileOp.DeleteItem(shellItem, null);

                        var updatedItem = items[i];
                        updatedItem.Queued = true;
                        items[i] = updatedItem;

                        queuedCount++;
                    }
                    catch (Exception)
                    {
                        if (throwOnError) throw;
                        (failedList ??= []).Add(item.OriginalPath);
                    }
                    finally
                    {
                        if (shellItem != null)
                        {
                            Marshal.ReleaseComObject(shellItem);
                        }
                    }
                }

                if (queuedCount > 0)
                {
                    try
                    {
                        createdFileOp.PerformOperations();

                        BOOL aborted = default;
                        createdFileOp.GetAnyOperationsAborted(&aborted);
                        if (aborted)
                        {
                            throw new OperationCanceledException("Операция удаления в корзину была прервана системой.");
                        }
                    }
                    catch (Exception) when (!throwOnError)
                    {
                        // Ошибки перехватываются — фактический статус файлов будет проверен ниже
                    }
                }
            }
            finally
            {
                if (fileOp != null)
                {
                    Marshal.ReleaseComObject(fileOp);
                }
            }
        }
        finally
        {
            if (needUninitialize)
            {
                PInvoke.CoUninitialize();
            }
        }

        // 3. Проверка фактического удаления всех поставленных в очередь файлов
        for (var i = 0; i < items.Count; i++)
        {
            var item = items[i];
            if (item.Queued && (File.Exists(item.FullPath) || Directory.Exists(item.FullPath)))
            {
                if (throwOnError)
                {
                    throw new IOException($"Не удалось переместить в корзину: '{item.FullPath}'.");
                }

                (failedList ??= []).Add(item.OriginalPath);
            }
        }
    }
}