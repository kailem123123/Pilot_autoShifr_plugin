using Ascon.Pilot.SDK;
using Ascon.Pilot.SDK.Menu;
using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace PilotProjectCipherPlugin
{
    [Export(typeof(IMenu<ObjectsViewContext>))]
    public class ProjectCipherPlugin : IMenu<ObjectsViewContext>
    {
        private const string CommandName = "cmd_set_project_cipher";
        private const string TargetMarker = "П-00-0000-000";
        private const string CodeAttribute = "code";

        private readonly IObjectModifier _modifier;
        private readonly IObjectsRepository _repository;

        // Внедрение зависимостей Pilot SDK
        [ImportingConstructor]
        public ProjectCipherPlugin(IObjectModifier modifier, IObjectsRepository repository)
        {
            _modifier = modifier;
            _repository = repository;
        }

        public void Build(IMenuBuilder builder, ObjectsViewContext context)
        {
            if (context == null || context.SelectedObjects == null || !context.SelectedObjects.Any())
                return;

            builder.AddItem(CommandName, 0)
                   .WithHeader("Задать шифр проекта");
        }

        // Ключевое изменение: async void для безопасной асинхронной работы в UI-потоке
        public async void OnMenuItemClick(string name, ObjectsViewContext context)
        {
            if (name != CommandName)
                return;

            var rootProject = context.SelectedObjects.FirstOrDefault();
            if (rootProject == null)
                return;

            string newCipher = ShowInputDialog("Введите актуальный шифр проекта:", "Задать шифр проекта");
            if (string.IsNullOrWhiteSpace(newCipher))
                return;

            try
            {
                // Запуск асинхронного обхода
                int updatedCount = await UpdateCipherRecursivelyBFSAsync(rootProject, newCipher);

                if (updatedCount == 0)
                {
                    _modifier.Clear();
                    MessageBox.Show("Маркер шифра 'П-00-0000-000' не найден ни в одном вложенном элементе.",
                                    "Результат", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }

                // Фиксация транзакции на сервере
                _modifier.Apply();

                MessageBox.Show($"Шифр успешно обновлен!\nИзменено объектов: {updatedCount}",
                                "Успех", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                _modifier.Clear();
                MessageBox.Show($"Произошла ошибка при обновлении шифров:\n{ex.Message}", "Ошибка", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        /// <summary>
        /// Асинхронный обход дерева в ширину (BFS).
        /// </summary>
        private async Task<int> UpdateCipherRecursivelyBFSAsync(Ascon.Pilot.SDK.IDataObject rootObject, string newCipher)
        {
            var queue = new Queue<Ascon.Pilot.SDK.IDataObject>();
            queue.Enqueue(rootObject);

            int editedCount = 0;

            while (queue.Count > 0)
            {
                var currentObject = queue.Dequeue();

                // 1. Фильтрация и модификация целевых типов
                if (currentObject.Type != null)
                {
                    string typeName = currentObject.Type.Name;

                    // Обновляем шифр только у нужных объектов
                    if (typeName == "project" || typeName == "section" || typeName == "subsection" || typeName == "document" || typeName == "main_set")
                    {
                        if (currentObject.Attributes != null && currentObject.Attributes.ContainsKey(CodeAttribute))
                        {
                            var codeValue = currentObject.Attributes[CodeAttribute];
                            if (codeValue is string codeStr && codeStr.Contains(TargetMarker))
                            {
                                // Заменяет только маркер, сохраняя суффиксы
                                string updatedCode = codeStr.Replace(TargetMarker, newCipher);

                                _modifier.Edit(currentObject).SetAttribute(CodeAttribute, updatedCode);
                                editedCount++;
                            }
                        }
                    }
                }

                // 2. Обработка дочерних элементов (проходим сквозь new_folder, main_set и т.д.)
                if (currentObject.Children != null)
                {
                    var childIds = currentObject.Children.OfType<Guid>().ToList();
                    if (childIds.Any())
                    {
                        // AWAIT уступает UI-поток Pilot-серверу для диспетчеризации
                        var loadedChildren = await LoadObjectsAsync(childIds);

                        foreach (var child in loadedChildren)
                        {
                            queue.Enqueue(child);
                        }
                    }
                }
            }

            return editedCount;
        }

        /// <summary>
        /// Официальный паттерн использования базового IObservable
        /// </summary>
        private async Task<List<Ascon.Pilot.SDK.IDataObject>> LoadObjectsAsync(List<Guid> ids)
        {
            var tcs = new TaskCompletionSource<List<Ascon.Pilot.SDK.IDataObject>>();

            // Создаем явный экземпляр IObserver
            var observer = new AsyncObjectObserver(ids, tcs);

            // Используем ЕДИНСТВЕННУЮ доступную сигнатуру: Subscribe(IObserver<T>)
            IDisposable subscription = _repository.SubscribeObjects(ids).Subscribe(observer);

            // Передаем подписку внутрь, чтобы Observer мог сам её отменить при завершении
            observer.SetSubscription(subscription);

            // Предохранитель: максимальное ожидание 15 секунд на один уровень
            var timeoutTask = Task.Delay(TimeSpan.FromSeconds(15));
            var completedTask = await Task.WhenAny(tcs.Task, timeoutTask);

            if (completedTask == timeoutTask)
            {
                subscription?.Dispose();
                return observer.GetCurrentResults(); // Возвращаем то, что успели загрузить
            }

            return await tcs.Task;
        }

        private string ShowInputDialog(string text, string caption)
        {
            using (Form prompt = new Form())
            {
                prompt.Width = 400;
                prompt.Height = 160;
                prompt.FormBorderStyle = FormBorderStyle.FixedDialog;
                prompt.Text = caption;
                prompt.StartPosition = FormStartPosition.CenterScreen;
                prompt.MaximizeBox = false;
                prompt.MinimizeBox = false;

                Label textLabel = new Label() { Left = 20, Top = 20, Width = 340, Text = text };
                TextBox textBox = new TextBox() { Left = 20, Top = 45, Width = 340 };
                Button confirmation = new Button() { Text = "ОК", Left = 260, Top = 80, Width = 100, DialogResult = DialogResult.OK };

                prompt.Controls.Add(textLabel);
                prompt.Controls.Add(textBox);
                prompt.Controls.Add(confirmation);
                prompt.AcceptButton = confirmation;

                return prompt.ShowDialog() == DialogResult.OK ? textBox.Text.Trim() : string.Empty;
            }
        }
    }

    /// <summary>
    /// Нативная реализация IObserver для связки IObservable и TaskCompletionSource
    /// </summary>
    internal class AsyncObjectObserver : IObserver<Ascon.Pilot.SDK.IDataObject>
    {
        private readonly List<Guid> _expectedIds;
        private readonly TaskCompletionSource<List<Ascon.Pilot.SDK.IDataObject>> _tcs;
        private readonly List<Ascon.Pilot.SDK.IDataObject> _results = new List<Ascon.Pilot.SDK.IDataObject>();
        private IDisposable _subscription;

        public AsyncObjectObserver(List<Guid> expectedIds, TaskCompletionSource<List<Ascon.Pilot.SDK.IDataObject>> tcs)
        {
            _expectedIds = expectedIds;
            _tcs = tcs;
        }

        public void SetSubscription(IDisposable subscription)
        {
            _subscription = subscription;
        }

        public List<Ascon.Pilot.SDK.IDataObject> GetCurrentResults()
        {
            return _results;
        }

        public void OnNext(Ascon.Pilot.SDK.IDataObject value)
        {
            // Берем только полностью загруженные карточки объектов
            if (value != null && value.State == Ascon.Pilot.SDK.DataState.Loaded)
            {
                lock (_results)
                {
                    if (!_results.Any(x => x.Id == value.Id))
                    {
                        _results.Add(value);

                        // Как только все ожидаемые Guid получены со статусом Loaded
                        if (_results.Count >= _expectedIds.Count)
                        {
                            _subscription?.Dispose();
                            _tcs.TrySetResult(_results);
                        }
                    }
                }
            }
        }

        public void OnError(Exception error)
        {
            _subscription?.Dispose();
            _tcs.TrySetException(error);
        }

        public void OnCompleted()
        {
            _subscription?.Dispose();
            _tcs.TrySetResult(_results);
        }
    }
}